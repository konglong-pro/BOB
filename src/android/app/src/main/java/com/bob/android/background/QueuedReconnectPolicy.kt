package com.bob.android.background

import com.bob.android.application.BobConnectionPhase

internal const val QUEUED_RECONNECT_WAIT_MILLIS = 60_000L

internal fun shouldKeepStartedTransferService(
    serviceStarted: Boolean,
    phase: BobConnectionPhase,
    elapsedMillis: Long?,
): Boolean = serviceStarted && phase in setOf(
    BobConnectionPhase.Connected, BobConnectionPhase.Retrying, BobConnectionPhase.Securing,
    BobConnectionPhase.OpeningSession, BobConnectionPhase.Synchronizing,
) && (elapsedMillis == null || elapsedMillis in 0 until QUEUED_RECONNECT_WAIT_MILLIS)
