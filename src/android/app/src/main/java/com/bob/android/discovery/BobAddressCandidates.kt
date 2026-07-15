package com.bob.android.discovery

import java.net.Inet4Address
import java.net.InetAddress

/** Android-free input used to rank resolved service addresses for one network. */
internal data class BobNetworkPrefix(
    val addressBytes: ByteArray,
    val prefixLength: Int,
) {
    init {
        require(addressBytes.size == 4 || addressBytes.size == 16)
        require(prefixLength in 0..(addressBytes.size * 8))
    }
}

/**
 * Keeps every DNS-SD address while preferring addresses on the phone's directly connected link.
 * Stable source ordering is retained inside each rank so the connection layer can probe in order.
 */
internal fun rankBobAddressCandidates(
    addresses: List<InetAddress>,
    localPrefixes: List<BobNetworkPrefix>,
): List<String> = addresses
    .mapIndexedNotNull { index, address ->
        val host = address.hostAddress?.takeIf(String::isNotBlank) ?: return@mapIndexedNotNull null
        RankedAddress(
            host = host,
            isOnLink = localPrefixes.any { prefix -> prefix.contains(address.address) },
            isIpv4 = address is Inet4Address,
            sourceIndex = index,
        )
    }
    .distinctBy { it.host.lowercase() }
    .sortedWith(
        compareByDescending<RankedAddress>(RankedAddress::isOnLink)
            .thenByDescending(RankedAddress::isIpv4)
            .thenBy(RankedAddress::sourceIndex),
    )
    .map(RankedAddress::host)

private data class RankedAddress(
    val host: String,
    val isOnLink: Boolean,
    val isIpv4: Boolean,
    val sourceIndex: Int,
)

private fun BobNetworkPrefix.contains(candidate: ByteArray): Boolean {
    if (addressBytes.size != candidate.size) return false
    val fullBytes = prefixLength / 8
    val remainingBits = prefixLength % 8
    for (index in 0 until fullBytes) {
        if (addressBytes[index] != candidate[index]) return false
    }
    if (remainingBits == 0) return true
    val mask = (0xFF shl (8 - remainingBits)) and 0xFF
    return (addressBytes[fullBytes].toInt() and mask) ==
        (candidate[fullBytes].toInt() and mask)
}
