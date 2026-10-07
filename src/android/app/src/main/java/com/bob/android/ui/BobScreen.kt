package com.bob.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.net.Uri
import android.os.CancellationSignal
import android.os.SystemClock
import android.util.Size
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.InputTransformation
import androidx.compose.foundation.text.input.TextFieldLineLimits
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size as CanvasSize
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.LifecycleStartEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.bob.android.network.BobTransferKind
import com.bob.android.ui.theme.BobBackground
import com.bob.android.ui.theme.BobBorder
import com.bob.android.ui.theme.BobButton
import com.bob.android.ui.theme.BobCard
import com.bob.android.ui.theme.BobInk
import com.bob.android.ui.theme.BobSecondaryInk
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun BobApp(viewModel: BobViewModel = viewModel()) {
    val context = LocalContext.current
    val imagePicker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(),
    ) { uris ->
        viewModel.queueImages(uris)
    }
    val filePicker = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments(),
    ) { uris ->
        viewModel.queueFiles(uris)
    }
    LifecycleStartEffect(viewModel) {
        viewModel.startDiscovery()
        onStopOrDispose { viewModel.stopDiscovery() }
    }

    val state by viewModel.uiState.collectAsStateWithLifecycle()
    BobScreen(
        state = state,
        manualIpInput = viewModel.manualIpInput,
        manualIpTransformation = viewModel.manualIpTransformation,
        draftInput = viewModel.draftInput,
        draftTransformation = viewModel.draftTransformation,
        onRefresh = viewModel::refresh,
        onSelectPeer = viewModel::selectPeer,
        onUseManualIp = viewModel::useManualIp,
        onRequestConnection = viewModel::requestConnection,
        onDisconnect = viewModel::disconnect,
        onResetTrust = viewModel::resetTrust,
        onSendText = viewModel::sendText,
        onCancelTransfer = viewModel::cancelTransfer,
        onRetryTransfer = viewModel::retryTransfer,
        onChooseImages = {
            if (viewModel.beginExternalContentSelection(BobTransferKind.Image)) {
                runCatching {
                    imagePicker.launch(
                        PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
                    )
                }.onFailure {
                    viewModel.endExternalContentSelection()
                    Toast.makeText(context, "Could not open the image picker", Toast.LENGTH_SHORT).show()
                }
            }
        },
        onChooseFiles = {
            if (viewModel.beginExternalContentSelection(BobTransferKind.File)) {
                runCatching { filePicker.launch(arrayOf("*/*")) }.onFailure {
                    viewModel.endExternalContentSelection()
                    Toast.makeText(context, "Could not open the file picker", Toast.LENGTH_SHORT).show()
                }
            }
        },
    )
}

@Composable
private fun BobScreen(
    state: BobUiState,
    manualIpInput: TextFieldState,
    manualIpTransformation: InputTransformation,
    draftInput: TextFieldState,
    draftTransformation: InputTransformation,
    onRefresh: () -> Unit,
    onSelectPeer: (String) -> Unit,
    onUseManualIp: () -> Unit,
    onRequestConnection: () -> Unit,
    onDisconnect: () -> Unit,
    onResetTrust: () -> Unit,
    onSendText: () -> Unit,
    onCancelTransfer: (String) -> Unit,
    onRetryTransfer: (String) -> Unit,
    onChooseImages: () -> Unit,
    onChooseFiles: () -> Unit,
) {
    var showConnectionPanel by rememberSaveable { mutableStateOf(false) }
    val timeline = remember(state.timeline, state.transfers) {
        buildList<ConversationEntry> {
            state.timeline.forEach { add(ConversationEntry.Message(it)) }
            state.transfers.forEach { add(ConversationEntry.Transfer(it)) }
        }.sortedBy(ConversationEntry::recordedAtEpochMillis)
    }
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val isNearLatest by remember {
        derivedStateOf {
            val lastVisible = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            lastVisible >= listState.layoutInfo.totalItemsCount - 3
        }
    }
    var followLatest by remember { mutableStateOf(true) }
    var previousLastKey by remember { mutableStateOf<String?>(null) }
    var initialTimelineShown by remember { mutableStateOf(false) }
    val latestKey = timeline.lastOrNull()?.key

    LaunchedEffect(listState) {
        snapshotFlow { listState.isScrollInProgress }.collect { scrolling ->
            if (!scrolling) followLatest = isNearLatest
        }
    }

    LaunchedEffect(latestKey, showConnectionPanel) {
        if (showConnectionPanel) return@LaunchedEffect
        if (latestKey == null) {
            initialTimelineShown = false
            previousLastKey = null
            return@LaunchedEffect
        }
        val newOutgoing = previousLastKey != latestKey && timeline.last().isOutgoing
        if (!initialTimelineShown || followLatest || newOutgoing) {
            listState.scrollToItem(timeline.size + 1)
        }
        initialTimelineShown = true
        previousLastKey = latestKey
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BobBackground)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .imePadding(),
    ) {
        ConversationHeader(
            state = state,
            showConnectionPanel = showConnectionPanel,
            onTogglePanel = { showConnectionPanel = !showConnectionPanel },
        )
        if (showConnectionPanel) {
            LazyColumn(
                modifier = Modifier.weight(1f),
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                item {
                    Text(
                        text = "Connection",
                        color = BobInk,
                        fontSize = 28.sp,
                        fontWeight = FontWeight.Bold,
                    )
                    Spacer(Modifier.height(6.dp))
                    Text(
                        text = "Keep BOB open on your computer and use the same Wi-Fi.",
                        color = BobSecondaryInk,
                        fontSize = 14.sp,
                        lineHeight = 20.sp,
                    )
                }
                item {
                    ConnectionSection(state, onRequestConnection, onDisconnect, onResetTrust)
                }
                state.notice?.let { message -> item { Notice(message) } }
                item { DeviceSection(state, onSelectPeer, onRefresh) }
                item { ManualAddressSection(manualIpInput, manualIpTransformation, onUseManualIp) }
                item {
                    Text(
                        text = "Received images: Pictures/BOB\nReceived files: Download/BOB",
                        color = BobSecondaryInk,
                        fontSize = 13.sp,
                        lineHeight = 20.sp,
                    )
                }
            }
        } else {
            Box(Modifier.weight(1f)) {
                LazyColumn(
                    state = listState,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(horizontal = 16.dp, vertical = 16.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    item(key = "conversation-context") {
                        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            Text(
                                text = "Computer on the left · Phone on the right",
                                color = BobSecondaryInk,
                                fontSize = 12.sp,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            if (state.connectionPhase != ConnectionUiPhase.Connected) {
                                ConnectionPrompt(state, onOpenConnection = { showConnectionPanel = true })
                            }
                            state.notice?.let { Notice(it) }
                            if (timeline.isNotEmpty()) {
                                Text(
                                    text = "Recent conversation · latest 20 text messages",
                                    color = BobSecondaryInk,
                                    fontSize = 12.sp,
                                )
                            }
                        }
                    }
                    if (timeline.isEmpty()) {
                        item(key = "empty-conversation") {
                            EmptyTimeline(state.connectionPhase == ConnectionUiPhase.Connected)
                        }
                    } else {
                        items(timeline.size, key = { timeline[it].key }) { index ->
                            val entry = timeline[index]
                            val date = entry.recordedAtEpochMillis.toConversationDate()
                            val previousDate = timeline.getOrNull(index - 1)
                                ?.recordedAtEpochMillis?.toConversationDate()
                            if (date != previousDate && date != null) {
                                ConversationDate(date)
                                Spacer(Modifier.height(10.dp))
                            }
                            when (entry) {
                                is ConversationEntry.Message -> TimelineCard(entry.item)
                                is ConversationEntry.Transfer -> TransferCard(
                                    item = entry.item,
                                    onCancel = { onCancelTransfer(entry.item.key) },
                                    onRetry = { onRetryTransfer(entry.item.key) },
                                )
                            }
                        }
                    }
                    item(key = "conversation-bottom") { Spacer(Modifier.height(32.dp)) }
                }
                if (timeline.isNotEmpty() && !isNearLatest) {
                    BobButton(
                        label = "Latest ↓",
                        onClick = {
                            followLatest = true
                            scope.launch { listState.animateScrollToItem(timeline.size + 1) }
                        },
                        modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 8.dp),
                    )
                }
            }
            TextComposer(
                state = state,
                draftInput = draftInput,
                draftTransformation = draftTransformation,
                onSendText = onSendText,
                onChooseImages = onChooseImages,
                onChooseFiles = onChooseFiles,
            )
        }
    }
}

@Composable
private fun ConversationHeader(
    state: BobUiState,
    showConnectionPanel: Boolean,
    onTogglePanel: () -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            ComputerGlyph(Modifier.size(36.dp))
            Column(Modifier.weight(1f)) {
                Text("BOB", color = BobInk, fontSize = 16.sp, fontWeight = FontWeight.Black)
                Text(
                    text = state.authenticatedPeerName ?: state.selectedTargetLabel ?: "Your computer",
                    color = BobInk,
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            BobButton(
                label = if (showConnectionPanel) "Chat" else "Connection",
                onClick = onTogglePanel,
            )
        }
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(BobCard)
                .border(1.dp, BobBorder)
                .semantics { liveRegion = LiveRegionMode.Polite }
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(7.dp),
        ) {
            Box(
                Modifier
                    .size(8.dp)
                    .then(
                        if (state.connectionPhase == ConnectionUiPhase.Connected) Modifier.background(BobInk)
                        else Modifier.border(1.dp, BobBorder),
                    ),
            )
            Text(
                text = state.connectionLabel,
                color = BobInk,
                fontSize = 12.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Text("PHONE ↔ PC", color = BobSecondaryInk, fontSize = 11.sp, fontWeight = FontWeight.Bold)
        }
    }
}

@Composable
private fun ConnectionPrompt(state: BobUiState, onOpenConnection: () -> Unit) {
    BorderedSection {
        Text(
            text = when (state.connectionPhase) {
                ConnectionUiPhase.Blocked -> "Connection needs attention"
                ConnectionUiPhase.Retrying -> "Reconnecting to your computer"
                ConnectionUiPhase.Securing, ConnectionUiPhase.Connecting, ConnectionUiPhase.Synchronizing -> "Connecting to your computer"
                else -> "Start a conversation with your computer"
            },
            color = BobInk,
            fontSize = 18.sp,
            fontWeight = FontWeight.Bold,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = if (state.connectionPhase == ConnectionUiPhase.Retrying) {
                "Keep both devices on the same Wi-Fi. BOB will try again automatically."
            } else if (state.connectionPhase == ConnectionUiPhase.Blocked || state.connectionPhase == ConnectionUiPhase.Failed) {
                "Open Connection to see what happened and reconnect."
            } else {
                "Open BOB on Windows, then find your computer in Connection."
            },
            color = BobSecondaryInk,
            fontSize = 14.sp,
            lineHeight = 20.sp,
        )
        Spacer(Modifier.height(8.dp))
        BobButton("Open connection", onOpenConnection)
    }
}

private sealed interface ConversationEntry {
    val key: String
    val recordedAtEpochMillis: Long
    val isOutgoing: Boolean

    data class Message(val item: TextTimelineItemUi) : ConversationEntry {
        override val key = "message:${item.key}"
        override val recordedAtEpochMillis = item.recordedAtEpochMillis
        override val isOutgoing = item.isOutgoing
    }

    data class Transfer(val item: TransferTimelineItemUi) : ConversationEntry {
        override val key = "transfer:${item.key}"
        override val recordedAtEpochMillis = item.recordedAtEpochMillis
        override val isOutgoing = item.isOutgoing
    }
}

private fun Long.toConversationDate(): LocalDate? = takeIf { it > 0L }
    ?.let { Instant.ofEpochMilli(it).atZone(ZoneId.systemDefault()).toLocalDate() }

@Composable
private fun ConversationDate(date: LocalDate) {
    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
        Text(
            text = if (date == LocalDate.now()) "Today" else date.format(CONVERSATION_DATE_FORMAT),
            color = BobSecondaryInk,
            fontSize = 12.sp,
            modifier = Modifier.border(1.dp, BobBorder, RoundedCornerShape(3.dp))
                .padding(horizontal = 12.dp, vertical = 5.dp),
        )
    }
}

@Composable
private fun ComputerGlyph(modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val stroke = 2.dp.toPx()
        drawRect(BobBorder, Offset(0f, size.height * 0.1f), CanvasSize(size.width, size.height * 0.62f), style = Stroke(stroke))
        drawLine(BobBorder, Offset(size.width * 0.5f, size.height * 0.72f), Offset(size.width * 0.5f, size.height * 0.9f), stroke)
        drawLine(BobBorder, Offset(size.width * 0.22f, size.height * 0.9f), Offset(size.width * 0.78f, size.height * 0.9f), stroke)
    }
}

@Composable
private fun BorderedSection(content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(BobCard)
            .border(2.dp, BobBorder, RoundedCornerShape(4.dp))
            .padding(18.dp),
        content = content,
    )
}

@Composable
private fun DeviceSection(
    state: BobUiState,
    onSelectPeer: (String) -> Unit,
    onRefresh: () -> Unit,
) {
    BorderedSection {
        Text(
            text = "Computers on this network",
            color = BobInk,
            fontSize = 20.sp,
            fontWeight = FontWeight.Bold,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = state.selectionGuidance,
            color = BobSecondaryInk,
            fontSize = 14.sp,
            lineHeight = 20.sp,
        )
        Spacer(Modifier.height(4.dp))
        Text(text = state.discoveryLabel, color = BobSecondaryInk, fontSize = 12.sp)
        Spacer(Modifier.height(16.dp))
        if (state.peers.isEmpty()) {
            EmptyDiscoveryState()
        } else {
            Column(Modifier.selectableGroup()) {
                state.peers.forEachIndexed { index, peer ->
                    if (index > 0) Spacer(Modifier.height(10.dp))
                    PeerRow(peer = peer, onClick = { onSelectPeer(peer.key) })
                }
            }
        }
        Spacer(Modifier.height(16.dp))
        BobButton(label = "Search again", onClick = onRefresh)
    }
}

@Composable
private fun EmptyDiscoveryState() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
            .padding(horizontal = 14.dp, vertical = 20.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = "No running BOB app found on Windows",
            color = BobSecondaryInk,
            fontSize = 14.sp,
        )
    }
}

@Composable
private fun PeerRow(peer: PeerUiModel, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(3.dp))
            .background(if (peer.isSelected) BobButton else Color.Transparent)
            .border(
                width = if (peer.isSelected) 2.dp else 1.dp,
                color = BobBorder,
                shape = RoundedCornerShape(3.dp),
            )
            .selectable(
                selected = peer.isSelected,
                enabled = peer.isCompatible,
                role = Role.RadioButton,
                onClick = onClick,
            )
            .padding(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(18.dp)
                .border(2.dp, BobBorder, RoundedCornerShape(2.dp))
                .padding(3.dp),
        ) {
            if (peer.isSelected) {
                Box(Modifier.fillMaxSize().background(BobInk))
            }
        }
        Spacer(Modifier.size(12.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = peer.name,
                color = BobInk,
                fontSize = 16.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = peer.endpoint,
                color = BobSecondaryInk,
                fontSize = 13.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        if (!peer.isCompatible) {
            Text(text = "Protocol incompatible", color = BobSecondaryInk, fontSize = 12.sp)
        }
    }
}

@Composable
private fun ManualAddressSection(
    address: TextFieldState,
    inputTransformation: InputTransformation,
    onUseAddress: () -> Unit,
) {
    BorderedSection {
        Text(
            text = "Manual IP",
            color = BobInk,
            fontSize = 20.sp,
            fontWeight = FontWeight.Bold,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = "If mDNS is unavailable, enter the address shown by BOB on Windows. Port 42424 is fixed.",
            color = BobSecondaryInk,
            fontSize = 14.sp,
            lineHeight = 20.sp,
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            state = address,
            inputTransformation = inputTransformation,
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Windows IP address") },
            lineLimits = TextFieldLineLimits.SingleLine,
            placeholder = { Text("Example: 192.168.1.20") },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
            shape = RoundedCornerShape(3.dp),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = BobBorder,
                unfocusedBorderColor = BobBorder,
                focusedContainerColor = BobButton,
                unfocusedContainerColor = BobButton,
                cursorColor = BobInk,
            ),
        )
        Spacer(Modifier.height(10.dp))
        BobButton(label = "Use this IP", onClick = onUseAddress)
    }
}

@Composable
private fun Notice(message: String) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(BobButton)
            .border(2.dp, BobBorder, RoundedCornerShape(3.dp))
            .semantics { liveRegion = LiveRegionMode.Polite }
            .padding(14.dp),
    ) {
        Text(text = message, color = BobInk, fontSize = 14.sp, lineHeight = 20.sp)
    }
}

@Composable
private fun ConnectionSection(
    state: BobUiState,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onResetTrust: () -> Unit,
) {
    BorderedSection {
        Text(
            text = state.connectionLabel,
            color = BobInk,
            fontSize = 20.sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = state.authenticatedPeerName ?: state.selectedTargetLabel ?: "No target selected",
            color = BobInk,
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
        )
        Spacer(Modifier.height(4.dp))
        Text(
            text = state.connectionDetail,
            color = BobSecondaryInk,
            fontSize = 13.sp,
            lineHeight = 19.sp,
        )
        if (state.canRequestConnection || state.canDisconnect || state.canResetTrust) {
            Spacer(Modifier.height(14.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (state.canRequestConnection) {
                    BobButton(
                        label = if (state.connectionPhase == ConnectionUiPhase.Retrying) {
                            "Retry now"
                        } else {
                            "Connect"
                        },
                        onClick = onConnect,
                        modifier = Modifier.weight(1f),
                    )
                }
                if (state.canDisconnect) {
                    BobButton(
                        label = "Disconnect",
                        onClick = onDisconnect,
                        modifier = Modifier.weight(1f),
                    )
                }
                if (state.canResetTrust) {
                    BobButton(
                        label = "Reset trust",
                        onClick = onResetTrust,
                        modifier = Modifier.weight(1f),
                    )
                }
            }
        }
    }
}

@Composable
private fun EmptyTimeline(connected: Boolean) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 40.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(24.dp),
        ) {
            Box(
                Modifier.size(width = 28.dp, height = 42.dp)
                    .border(2.dp, BobBorder, RoundedCornerShape(3.dp))
                    .padding(5.dp),
                contentAlignment = Alignment.BottomCenter,
            ) {
                Box(Modifier.size(width = 10.dp, height = 2.dp).background(BobInk))
            }
            Text("↔", color = BobInk, fontSize = 32.sp)
            ComputerGlyph(Modifier.size(48.dp))
        }
        Spacer(Modifier.height(24.dp))
        Text(
            text = if (connected) "Your conversation starts here" else "Phone meets computer",
            color = BobInk,
            fontSize = 20.sp,
            fontWeight = FontWeight.Bold,
        )
        Spacer(Modifier.height(8.dp))
        Text(
            text = if (connected) {
                "Send a message, image, or file below. Everything stays on your local network."
            } else {
                "Messages and files appear together, in the order you send and receive them."
            },
            color = BobSecondaryInk,
            fontSize = 14.sp,
            lineHeight = 21.sp,
        )
    }
}

@Composable
private fun ConversationBubble(
    isOutgoing: Boolean,
    content: @Composable ColumnScope.() -> Unit,
) {
    Box(
        modifier = Modifier.fillMaxWidth(),
        contentAlignment = if (isOutgoing) Alignment.CenterEnd else Alignment.CenterStart,
    ) {
        val shape = if (isOutgoing) {
            RoundedCornerShape(topStart = 4.dp, topEnd = 0.dp, bottomEnd = 4.dp, bottomStart = 4.dp)
        } else {
            RoundedCornerShape(topStart = 0.dp, topEnd = 4.dp, bottomEnd = 4.dp, bottomStart = 4.dp)
        }
        Column(
            modifier = Modifier
                .widthIn(max = 560.dp)
                .fillMaxWidth(0.9f)
                .background(if (isOutgoing) BobButton else BobCard, shape)
                .border(1.dp, BobBorder, shape)
                .padding(12.dp),
            content = content,
        )
    }
}

@Composable
private fun MessageDirection(isOutgoing: Boolean, kind: String? = null) {
    Text(
        text = buildString {
            append(if (isOutgoing) "PHONE → COMPUTER" else "COMPUTER → PHONE")
            kind?.let { append(" · ").append(it.uppercase(Locale.ROOT)) }
        },
        color = BobSecondaryInk,
        fontSize = 11.sp,
        fontWeight = FontWeight.Bold,
    )
}

@Composable
private fun MessageStatus(time: String, status: String) {
    Text(
        text = listOf(time, status).filter(String::isNotBlank).joinToString(" · "),
        color = BobSecondaryInk,
        fontSize = 12.sp,
        lineHeight = 18.sp,
    )
}

@Composable
private fun TimelineCard(item: TextTimelineItemUi) {
    val context = LocalContext.current
    var previewOverflows by remember(item.key) { mutableStateOf(false) }
    var showFullText by rememberSaveable(item.key) { mutableStateOf(false) }
    ConversationBubble(isOutgoing = item.isOutgoing) {
        MessageDirection(item.isOutgoing)
        Spacer(Modifier.height(8.dp))
        SelectionContainer {
            Text(
                text = item.previewText,
                color = BobInk,
                fontSize = 16.sp,
                lineHeight = 23.sp,
                maxLines = 8,
                overflow = TextOverflow.Ellipsis,
                onTextLayout = { previewOverflows = it.hasVisualOverflow },
            )
        }
        Spacer(Modifier.height(8.dp))
        MessageStatus(item.timeLabel, item.statusLabel)
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            BobButton(
                label = "Copy text",
                onClick = { copyTextToClipboard(context, item.text) },
                modifier = Modifier.weight(1f),
            )
            if (item.isTruncated || previewOverflows) {
                BobButton(
                    label = "View text",
                    onClick = { showFullText = true },
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
    if (showFullText) {
        FullTextDialog(
            text = item.text,
            onDismiss = { showFullText = false },
        )
    }
}

@Composable
private fun TransferCard(item: TransferTimelineItemUi, onCancel: () -> Unit, onRetry: () -> Unit) {
    val context = LocalContext.current
    val previewUri = item.previewUri
    var showFullImage by rememberSaveable(item.key, item.previewUri) { mutableStateOf(false) }
    val thumbnail = previewUri?.let { uri ->
        rememberContentBitmap(uri, THUMBNAIL_MAX_PIXELS)
    }
    val rate = rememberTransferRate(item)
    ConversationBubble(isOutgoing = item.isOutgoing) {
        MessageDirection(item.isOutgoing, item.kindLabel)
        Spacer(Modifier.height(8.dp))
        Text(
            text = item.name,
            color = BobInk,
            fontSize = 16.sp,
            lineHeight = 22.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
        )
        if (thumbnail != null) {
            Spacer(Modifier.height(10.dp))
            Image(
                bitmap = remember(thumbnail) { thumbnail.asImageBitmap() },
                contentDescription = "Preview of ${item.name}",
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 100.dp, max = 220.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(BobButton)
                    .clickable(
                        role = Role.Button,
                        onClickLabel = "View full image",
                        onClick = { showFullImage = true },
                    )
                    .border(1.dp, BobBorder, RoundedCornerShape(3.dp)),
            )
        }
        Spacer(Modifier.height(8.dp))
        Text(text = item.progressLabel, color = BobInk, fontSize = 13.sp)
        if (item.isActive || item.statusLabel == "Completed") {
            Spacer(Modifier.height(8.dp))
            val total = item.totalBytes
            val fraction = when {
                item.statusLabel == "Completed" -> 1f
                total != null && total > 0L -> (item.bytesTransferred.toDouble() / total.toDouble()).coerceIn(0.0, 1.0).toFloat()
                else -> null
            }
            if (fraction != null) {
                LinearProgressIndicator(
                    progress = { fraction },
                    modifier = Modifier.fillMaxWidth().height(6.dp)
                        .semantics { contentDescription = "Transfer progress" },
                    color = BobInk,
                    trackColor = BobBackground,
                )
            } else {
                LinearProgressIndicator(
                    modifier = Modifier.fillMaxWidth().height(6.dp),
                    color = BobInk,
                    trackColor = BobBackground,
                )
            }
        }
        if (item.statusLabel == "Transferring") {
            Spacer(Modifier.height(5.dp))
            Text(
                text = rate?.toLabel(item) ?: "Estimating speed…",
                color = BobSecondaryInk,
                fontSize = 12.sp,
                lineHeight = 18.sp,
            )
        }
        Spacer(Modifier.height(6.dp))
        MessageStatus(item.timeLabel, item.statusLabel)
        item.errorMessage?.let { error ->
            Spacer(Modifier.height(6.dp))
            Text(
                text = error,
                color = BobInk,
                fontSize = 13.sp,
                lineHeight = 18.sp,
                modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
            )
        }
        if (previewUri != null) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (thumbnail != null) {
                    BobButton(
                        label = "View image",
                        onClick = { showFullImage = true },
                        modifier = Modifier.weight(1f),
                    )
                }
                BobButton(
                    label = "Copy image",
                    onClick = { copyImageToClipboard(context, item.name, previewUri) },
                    modifier = Modifier.weight(1f),
                )
            }
        }
        if (item.canCancel || item.canRetry) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (item.canCancel) BobButton("Cancel", onCancel, modifier = Modifier.weight(1f))
                if (item.canRetry) BobButton("Retry", onRetry, modifier = Modifier.weight(1f))
            }
        }
    }
    if (showFullImage && thumbnail != null) {
        FullImageDialog(
            name = item.name,
            previewUri = previewUri,
            thumbnail = thumbnail,
            onDismiss = { showFullImage = false },
        )
    }
}

@Composable
private fun FullImageDialog(
    name: String,
    previewUri: String,
    thumbnail: Bitmap,
    onDismiss: () -> Unit,
) {
    val context = LocalContext.current
    val fullImage = rememberContentBitmap(
        uriValue = previewUri,
        maxDimension = FULL_IMAGE_MAX_PIXELS,
        fallback = thumbnail,
    ) ?: thumbnail
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(20.dp)
                .background(BobCard)
                .border(2.dp, BobBorder, RoundedCornerShape(4.dp))
                .padding(16.dp),
        ) {
            Text(
                text = "Image preview",
                color = BobInk,
                fontSize = 20.sp,
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = name,
                color = BobSecondaryInk,
                fontSize = 14.sp,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(12.dp))
            Image(
                bitmap = remember(fullImage) { fullImage.asImageBitmap() },
                contentDescription = "Full image preview of $name",
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .background(Color.Black)
                    .border(1.dp, BobBorder, RoundedCornerShape(3.dp)),
            )
            Spacer(Modifier.height(12.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                BobButton(
                    label = "Copy image",
                    onClick = { copyImageToClipboard(context, name, previewUri) },
                    modifier = Modifier.weight(1f),
                )
                BobButton(
                    label = "Close",
                    onClick = onDismiss,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

private data class TransferRate(val bytesPerSecond: Double, val isStalled: Boolean) {
    fun toLabel(item: TransferTimelineItemUi): String {
        if (isStalled) return "Waiting for data…"
        if (bytesPerSecond <= 0.0) return "Estimating speed…"
        val speed = "${formatDisplayBytes(bytesPerSecond.toLong())}/s"
        val remaining = item.totalBytes?.takeIf { it > item.bytesTransferred }?.let { total ->
            ((total - item.bytesTransferred.coerceAtLeast(0L)) / bytesPerSecond).toLong()
        }
        return if (remaining != null) "$speed · ${remaining.toRemainingTime()}" else speed
    }
}

@Composable
private fun rememberTransferRate(item: TransferTimelineItemUi): TransferRate? {
    val latestBytes by rememberUpdatedState(item.bytesTransferred)
    val transferring = item.isActive && item.statusLabel == "Transferring"
    val rate by produceState<TransferRate?>(null, item.key, transferring) {
        value = null
        if (!transferring) return@produceState
        var sampledBytes = latestBytes
        var sampledAt = SystemClock.elapsedRealtime()
        var lastProgressAt = sampledAt
        var smoothedRate = 0.0
        while (true) {
            delay(1_000L)
            val now = SystemClock.elapsedRealtime()
            val currentBytes = latestBytes
            val delta = (currentBytes - sampledBytes).coerceAtLeast(0L)
            val elapsed = (now - sampledAt).coerceAtLeast(1L)
            val sample = delta.toDouble() * 1_000.0 / elapsed.toDouble()
            if (delta > 0L) lastProgressAt = now
            smoothedRate = if (smoothedRate == 0.0) sample else smoothedRate * 0.7 + sample * 0.3
            value = TransferRate(smoothedRate, now - lastProgressAt >= 3_000L)
            sampledBytes = currentBytes
            sampledAt = now
        }
    }
    return rate
}

private fun formatDisplayBytes(bytes: Long): String {
    val value = bytes.coerceAtLeast(0L)
    val units = arrayOf("B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB")
    var amount = value.toDouble()
    var unit = 0
    while (amount >= 1024.0 && unit < units.lastIndex) {
        amount /= 1024.0
        unit += 1
    }
    return if (unit == 0) "$value B" else String.format(Locale.ROOT, "%.1f %s", amount, units[unit])
}

private fun Long.toRemainingTime(): String = when {
    this < 60L -> "${coerceAtLeast(1L)}s left"
    this < 3_600L -> "${this / 60L}m ${this % 60L}s left"
    this < 86_400L -> "${this / 3_600L}h ${(this % 3_600L) / 60L}m left"
    else -> "${this / 86_400L}d ${(this % 86_400L) / 3_600L}h left"
}

@Composable
private fun rememberContentBitmap(
    uriValue: String,
    maxDimension: Int,
    fallback: Bitmap? = null,
): Bitmap? {
    val resolver = LocalContext.current.contentResolver
    val bitmap by produceState<Bitmap?>(
        initialValue = fallback,
        uriValue,
        maxDimension,
    ) {
        value = withContext(Dispatchers.IO) {
            val cancellationSignal = CancellationSignal()
            val cancellationHandle = coroutineContext[Job]?.invokeOnCompletion {
                cancellationSignal.cancel()
            }
            try {
                runCatching {
                    resolver.loadThumbnail(
                        Uri.parse(uriValue),
                        Size(maxDimension, maxDimension),
                        cancellationSignal,
                    )
                }.getOrNull()
            } finally {
                cancellationHandle?.dispose()
            }
        } ?: fallback
    }
    return bitmap
}

@Composable
private fun FullTextDialog(text: String, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val chunks = remember(text) { text.toDisplayChunks() }
    Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 240.dp, max = 560.dp)
                .background(BobCard)
                .border(2.dp, BobBorder, RoundedCornerShape(4.dp))
                .padding(18.dp),
        ) {
            Text(
                text = "Full text",
                color = BobInk,
                fontSize = 20.sp,
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(12.dp))
            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
                    .padding(12.dp),
            ) {
                items(chunks, key = { it.index }) { chunk ->
                    SelectionContainer {
                        Text(
                            text = chunk.text,
                            color = BobInk,
                            fontSize = 16.sp,
                            lineHeight = 23.sp,
                        )
                    }
                }
            }
            Spacer(Modifier.height(12.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                BobButton(
                    label = "Copy full text",
                    onClick = { copyTextToClipboard(context, text) },
                    modifier = Modifier.weight(1f),
                )
                BobButton(
                    label = "Close",
                    onClick = onDismiss,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

@Composable
private fun TextComposer(
    state: BobUiState,
    draftInput: TextFieldState,
    draftTransformation: InputTransformation,
    onSendText: () -> Unit,
    onChooseImages: () -> Unit,
    onChooseFiles: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(BobBackground)
            .border(1.dp, BobBorder)
            .heightIn(max = 230.dp)
            .padding(horizontal = 16.dp, vertical = 12.dp)
            .verticalScroll(rememberScrollState()),
    ) {
        OutlinedTextField(
            state = draftInput,
            inputTransformation = draftTransformation,
            modifier = Modifier.fillMaxWidth(),
            placeholder = { Text("Message your computer") },
            isError = state.draftError != null,
            lineLimits = TextFieldLineLimits.MultiLine(minHeightInLines = 1, maxHeightInLines = 4),
            shape = RoundedCornerShape(3.dp),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = BobBorder,
                unfocusedBorderColor = BobBorder,
                focusedContainerColor = BobButton,
                unfocusedContainerColor = BobButton,
                cursorColor = BobInk,
            ),
        )
        state.draftError?.let { error ->
            Spacer(Modifier.height(4.dp))
            Text(
                text = error,
                color = BobInk,
                fontSize = 12.sp,
                modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
            )
        }
        Spacer(Modifier.height(8.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            BobButton(
                label = "Images",
                onClick = onChooseImages,
                modifier = Modifier.weight(1f),
                enabled = state.canChooseContent,
            )
            BobButton(
                label = "Files",
                onClick = onChooseFiles,
                modifier = Modifier.weight(1f),
                enabled = state.canChooseContent,
            )
            BobButton(
                label = if (state.isQueueingText) "Saving…" else "Send ↑",
                onClick = onSendText,
                enabled = state.canSendText,
                modifier = Modifier.weight(1.2f),
                prominent = true,
            )
        }
        if (!state.canChooseContent) {
            Spacer(Modifier.height(4.dp))
            Text("Connect to send messages and files", color = BobSecondaryInk, fontSize = 12.sp)
        }
    }
}

private data class DisplayTextChunk(val index: Int, val text: String)

private fun String.toDisplayChunks(): List<DisplayTextChunk> {
    if (isEmpty()) return listOf(DisplayTextChunk(0, ""))
    val chunks = ArrayList<DisplayTextChunk>((length / DISPLAY_CHUNK_CHARS) + 1)
    var start = 0
    var index = 0
    while (start < length) {
        var end = minOf(start + DISPLAY_CHUNK_CHARS, length)
        if (end < length && this[end - 1].isHighSurrogate() && this[end].isLowSurrogate()) {
            end -= 1
        }
        chunks += DisplayTextChunk(index++, substring(start, end))
        start = end
    }
    return chunks
}

private fun copyTextToClipboard(context: Context, text: String) {
    val copied = runCatching {
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText("BOB text", text))
    }.isSuccess
    Toast.makeText(
        context,
        if (copied) "Text copied" else "Could not copy text",
        Toast.LENGTH_SHORT,
    ).show()
}

private fun copyImageToClipboard(context: Context, name: String, uriValue: String) {
    val copied = runCatching {
        val uri = Uri.parse(uriValue)
        require(uri.scheme == "content") { "Only local content URIs can be copied." }
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(
            ClipData.newUri(context.contentResolver, name.ifBlank { "BOB image" }, uri),
        )
    }.isSuccess
    Toast.makeText(
        context,
        if (copied) "Image copied" else "Could not copy image",
        Toast.LENGTH_SHORT,
    ).show()
}

private const val DISPLAY_CHUNK_CHARS = 512
private const val THUMBNAIL_MAX_PIXELS = 720
private const val FULL_IMAGE_MAX_PIXELS = 2_048
private val CONVERSATION_DATE_FORMAT = DateTimeFormatter.ofPattern("MMM d, yyyy", Locale.ENGLISH)

@Composable
private fun BobButton(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    fillWidth: Boolean = false,
    prominent: Boolean = false,
) {
    val interactionSource = remember { MutableInteractionSource() }
    val focused by interactionSource.collectIsFocusedAsState()
    val pressed by interactionSource.collectIsPressedAsState()
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier
            .then(if (fillWidth) Modifier.fillMaxWidth() else Modifier)
            .defaultMinSize(minHeight = 48.dp),
        shape = RoundedCornerShape(3.dp),
        border = if (enabled) BorderStroke(if (focused || pressed) 2.dp else 1.dp, BobBorder) else null,
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
        interactionSource = interactionSource,
        colors = ButtonDefaults.buttonColors(
            containerColor = if (prominent) BobInk else BobButton,
            contentColor = if (prominent) BobButton else BobInk,
            disabledContainerColor = BobButton.copy(alpha = 0.55f),
            disabledContentColor = BobSecondaryInk,
        ),
    ) {
        Text(text = label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
    }
}
