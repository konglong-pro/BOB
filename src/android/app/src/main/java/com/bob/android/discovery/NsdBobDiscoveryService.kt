package com.bob.android.discovery

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import androidx.annotation.MainThread
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Discovers BOB Windows peers advertised over DNS-SD on the current LAN. */
@MainThread
class NsdBobDiscoveryService(context: Context) : AutoCloseable {
    private val nsdManager = requireNotNull(context.getSystemService(NsdManager::class.java))
    private val connectivityManager = requireNotNull(
        context.getSystemService(ConnectivityManager::class.java),
    )
    private val callbackExecutor = context.mainExecutor
    private val resolvedByServiceName = linkedMapOf<String, DiscoveredComputer>()
    private val infoCallbacks = mutableMapOf<String, NsdManager.ServiceInfoCallback>()

    private val mutableSnapshot = MutableStateFlow(DiscoverySnapshot())
    val snapshot: StateFlow<DiscoverySnapshot> = mutableSnapshot.asStateFlow()

    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private var restartAfterStop = false
    private var stopInFlight = false
    private var generation = 0L
    private var closed = false

    fun start() {
        if (closed) return

        if (discoveryListener != null) {
            if (stopInFlight) {
                restartAfterStop = true
            } else {
                restartAfterStop = false
            }
            publish(phase = DiscoveryPhase.Searching, errorMessage = null)
            return
        }

        invalidateCurrentRound()
        publish(phase = DiscoveryPhase.Searching, errorMessage = null)
        val listenerGeneration = generation
        val listener = createDiscoveryListener(listenerGeneration)
        discoveryListener = listener
        try {
            nsdManager.discoverServices(
                SERVICE_TYPE,
                NsdManager.PROTOCOL_DNS_SD,
                null as Network?,
                callbackExecutor,
                listener,
            )
        } catch (_: RuntimeException) {
            if (discoveryListener === listener) {
                discoveryListener = null
                invalidateCurrentRound()
                publishFailure("Could not start local network discovery.")
            }
        }
    }

    fun refresh() {
        if (closed) return

        val activeListener = discoveryListener
        if (activeListener == null) {
            start()
            return
        }

        restartAfterStop = true
        publish(phase = DiscoveryPhase.Searching, errorMessage = null)
        if (stopInFlight) return

        requestStop(activeListener)
    }

    fun stop() {
        if (closed) return

        restartAfterStop = false
        val activeListener = discoveryListener
        if (activeListener == null) {
            invalidateCurrentRound()
            publish(phase = DiscoveryPhase.Idle, errorMessage = null)
            return
        }
        if (stopInFlight) return

        requestStop(activeListener)
    }

    private fun requestStop(activeListener: NsdManager.DiscoveryListener) {
        stopInFlight = true
        try {
            nsdManager.stopServiceDiscovery(activeListener)
        } catch (_: IllegalArgumentException) {
            stopInFlight = false
            if (discoveryListener === activeListener) {
                finishStopping()
            }
        } catch (_: RuntimeException) {
            stopInFlight = false
            if (!closed && discoveryListener === activeListener) {
                publishFailure("Could not stop local network discovery.")
            }
        }
    }

    private fun createDiscoveryListener(listenerGeneration: Long): NsdManager.DiscoveryListener =
        object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                if (!isCurrentDiscoveryListener(this, listenerGeneration)) return
                publish(phase = DiscoveryPhase.Searching, errorMessage = null)
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (!isCurrentDiscoveryListener(this, listenerGeneration)) return
                if (!isBobService(serviceInfo.serviceType)) return
                registerServiceInfoCallback(serviceInfo, listenerGeneration)
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                if (!isCurrentDiscoveryListener(this, listenerGeneration)) return
                removeService(serviceInfo.serviceName)
            }

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                if (!isCurrentDiscoveryListener(this, listenerGeneration)) return
                discoveryListener = null
                stopInFlight = false
                restartAfterStop = false
                invalidateCurrentRound()
                publishFailure("Local network discovery failed to start (error $errorCode)")
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                if (discoveryListener !== this) return
                stopInFlight = false
                if (!closed) {
                    publishFailure("Could not stop local network discovery (error $errorCode). Try again.")
                }
            }

            override fun onDiscoveryStopped(serviceType: String) {
                if (discoveryListener !== this) return
                finishStopping()
            }
        }

    private fun registerServiceInfoCallback(serviceInfo: NsdServiceInfo, callbackGeneration: Long) {
        if (closed || generation != callbackGeneration || stopInFlight) return

        val serviceName = serviceInfo.serviceName
        if (serviceName.isBlank() || infoCallbacks.containsKey(serviceName)) return

        val callback = object : NsdManager.ServiceInfoCallback {
            override fun onServiceInfoCallbackRegistrationFailed(errorCode: Int) {
                if (!isCurrentInfoCallback(serviceName, callbackGeneration, this)) return
                infoCallbacks.remove(serviceName, this)
            }

            override fun onServiceUpdated(updatedInfo: NsdServiceInfo) {
                if (!isCurrentInfoCallback(serviceName, callbackGeneration, this)) return
                upsertResolvedService(updatedInfo)
            }

            override fun onServiceLost() {
                if (!isCurrentInfoCallback(serviceName, callbackGeneration, this)) return
                removeService(serviceName)
            }

            override fun onServiceInfoCallbackUnregistered() {
                infoCallbacks.remove(serviceName, this)
            }
        }

        infoCallbacks[serviceName] = callback
        try {
            nsdManager.registerServiceInfoCallback(serviceInfo, callbackExecutor, callback)
        } catch (_: RuntimeException) {
            infoCallbacks.remove(serviceName, callback)
        }
    }

    private fun upsertResolvedService(serviceInfo: NsdServiceInfo) {
        val localPrefixes = serviceInfo.network
            ?.let(connectivityManager::getLinkProperties)
            ?.linkAddresses
            .orEmpty()
            .map { linkAddress ->
                BobNetworkPrefix(
                    addressBytes = linkAddress.address.address,
                    prefixLength = linkAddress.prefixLength,
                )
            }
        val hosts = rankBobAddressCandidates(serviceInfo.hostAddresses, localPrefixes)
        if (hosts.isEmpty()) return
        val serviceName = serviceInfo.serviceName.takeIf(String::isNotBlank) ?: return
        if (serviceInfo.port !in 1..65535) return

        val protocolVersion = serviceInfo.txt("pv") ?: ""
        val apiPath = serviceInfo.txt("api") ?: ""
        val usesTls = serviceInfo.txt("tls") == "1"
        val displayName = serviceInfo.txt("name")
            ?.takeIf(String::isNotBlank)
            ?: serviceName
        val deviceId = serviceInfo.txt("id")
            ?.takeIf(String::isNotBlank)
            ?: ""

        resolvedByServiceName[serviceName] = DiscoveredComputer(
            serviceName = serviceName,
            deviceId = deviceId,
            displayName = displayName,
            hosts = hosts,
            port = serviceInfo.port,
            protocolVersion = protocolVersion,
            apiPath = apiPath,
            usesTls = usesTls,
            network = serviceInfo.network,
        )
        publish(phase = DiscoveryPhase.Searching, errorMessage = null)
    }

    private fun NsdServiceInfo.txt(key: String): String? =
        attributes[key]?.toString(Charsets.UTF_8)?.trim()

    private fun removeService(serviceName: String) {
        val callback = infoCallbacks.remove(serviceName)
        if (callback != null) unregisterInfoCallback(callback)
        if (resolvedByServiceName.remove(serviceName) != null) {
            publish(phase = DiscoveryPhase.Searching, errorMessage = null)
        }
    }

    private fun clearInfoCallbacks() {
        val callbacks = infoCallbacks.values.toList()
        infoCallbacks.clear()
        callbacks.forEach(::unregisterInfoCallback)
    }

    private fun unregisterInfoCallback(callback: NsdManager.ServiceInfoCallback) {
        try {
            nsdManager.unregisterServiceInfoCallback(callback)
        } catch (_: RuntimeException) {
            // Already unregistered or the NSD service is shutting down.
        }
    }

    private fun invalidateCurrentRound() {
        generation += 1
        resolvedByServiceName.clear()
        clearInfoCallbacks()
    }

    private fun isCurrentDiscoveryListener(
        listener: NsdManager.DiscoveryListener,
        listenerGeneration: Long,
    ): Boolean =
        !closed &&
            !stopInFlight &&
            generation == listenerGeneration &&
            discoveryListener === listener

    private fun isCurrentInfoCallback(
        serviceName: String,
        callbackGeneration: Long,
        callback: NsdManager.ServiceInfoCallback,
    ): Boolean =
        !closed &&
            !stopInFlight &&
            generation == callbackGeneration &&
            infoCallbacks[serviceName] === callback

    private fun finishStopping() {
        discoveryListener = null
        stopInFlight = false
        invalidateCurrentRound()
        val shouldRestart = restartAfterStop
        restartAfterStop = false
        if (shouldRestart && !closed) {
            start()
        } else {
            publish(phase = DiscoveryPhase.Idle, errorMessage = null)
        }
    }

    private fun publishFailure(message: String) {
        publish(phase = DiscoveryPhase.Failed, errorMessage = message)
    }

    private fun publish(phase: DiscoveryPhase, errorMessage: String?) {
        mutableSnapshot.value = DiscoverySnapshot(
            phase = phase,
            devices = resolvedByServiceName.values
                .sortedBy { it.displayName.lowercase(Locale.ROOT) },
            errorMessage = errorMessage,
        )
    }

    private fun isBobService(serviceType: String): Boolean =
        serviceType.trimEnd('.').equals(SERVICE_TYPE.trimEnd('.'), ignoreCase = true)

    override fun close() {
        if (closed) return
        closed = true
        restartAfterStop = false
        invalidateCurrentRound()
        val listener = discoveryListener
        if (listener != null && !stopInFlight) {
            stopInFlight = true
            try {
                nsdManager.stopServiceDiscovery(listener)
            } catch (_: IllegalArgumentException) {
                if (discoveryListener === listener) discoveryListener = null
                stopInFlight = false
            } catch (_: RuntimeException) {
                // Keep the listener handle: a synchronous platform failure does not prove
                // that the original discovery registration has stopped.
                stopInFlight = false
            }
        }
        publish(phase = DiscoveryPhase.Idle, errorMessage = null)
    }

    private companion object {
        const val SERVICE_TYPE = "_bob._tcp."
    }
}
