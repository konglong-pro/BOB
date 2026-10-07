package com.bob.android.background

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.PowerManager
import com.bob.android.MainActivity
import com.bob.android.R
import com.bob.android.transfer.OnlineTransferItem
import com.bob.android.transfer.OnlineTransferState

/** Runs only while a user initiated transfer is offered, moving bytes, or being verified. */
class TransferForegroundService : Service() {
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "File transfers", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Progress and results for BOB file transfers"
                setSound(null, null)
                enableVibration(false)
            },
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val active = BobTransferRuntime.get(this).foregroundServiceItems()
        startForeground(NOTIFICATION_ID, notification(active), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        if (active.isEmpty()) {
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        updateLocks(active)
        return START_NOT_STICKY
    }

    private fun updateLocks(active: List<OnlineTransferItem>) {
        val moving = active.any { it.state != OnlineTransferState.Verifying }
        if (!moving) {
            if (wakeLock?.isHeld == true) wakeLock?.release()
            if (wifiLock?.isHeld == true) wifiLock?.release()
            return
        }
        if (wakeLock?.isHeld != true) wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "BOB:FileTransfer").apply {
                setReferenceCounted(false)
                acquire(6 * 60 * 60 * 1_000L)
            }
        @Suppress("DEPRECATION")
        if (wifiLock?.isHeld != true) wifiLock = getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "BOB:FileTransfer").apply {
                setReferenceCounted(false)
                acquire()
            }
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        BobTransferRuntime.get(this).serviceTimedOut()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        if (wakeLock?.isHeld == true) wakeLock?.release()
        if (wifiLock?.isHeld == true) wifiLock?.release()
        wakeLock = null
        wifiLock = null
        if (instance === this) instance = null
        super.onDestroy()
    }

    private fun notification(items: List<OnlineTransferItem>): Notification {
        val item = items.firstOrNull()
        val title = if (items.size > 1) "Transferring ${items.size} files" else "BOB file transfer"
        val waiting = item?.state in setOf(OnlineTransferState.Queued, OnlineTransferState.Completed,
            OnlineTransferState.Failed, OnlineTransferState.Canceled)
        val content = item?.let { if (waiting) "Waiting for next file · ${it.name}"
            else "${it.name} · ${it.state.name}" } ?: "Finishing transfer"
        val size = item?.size
        val progress = if (size != null && size > 0L) {
            (item.bytesTransferred.toDouble() / size.toDouble() * 100.0).toInt().coerceIn(0, 100)
        } else 0
        return builder(title, content)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setProgress(100, progress, size == null || waiting)
            .build()
    }

    private fun builder(title: String, text: String): Notification.Builder {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_bob_foreground)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(open)
            .setCategory(Notification.CATEGORY_PROGRESS)
            .setOnlyAlertOnce(true)
    }

    companion object {
        private const val CHANNEL_ID = "bob-file-transfers"
        private const val NOTIFICATION_ID = 1
        @Volatile private var instance: TransferForegroundService? = null

        fun update(items: List<OnlineTransferItem>) {
            instance?.let { service ->
                service.updateLocks(items)
                service.getSystemService(NotificationManager::class.java)
                    .notify(NOTIFICATION_ID, service.notification(items))
            }
        }

        fun finish(items: List<OnlineTransferItem>, appVisible: Boolean) {
            instance?.let { service ->
                service.stopForeground(STOP_FOREGROUND_REMOVE)
                val last = items.lastOrNull { it.state in setOf(OnlineTransferState.Completed,
                    OnlineTransferState.Failed, OnlineTransferState.Canceled) } ?: return
                if (!appVisible) {
                    service.getSystemService(NotificationManager::class.java).notify(
                        NOTIFICATION_ID,
                        service.builder("BOB transfer ${last.state.name.lowercase()}", last.name)
                            .setAutoCancel(true)
                            .build(),
                    )
                }
            }
        }
    }
}
