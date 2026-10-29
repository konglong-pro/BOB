package com.bob.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.os.CancellationSignal
import android.util.Size
import android.widget.Toast
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.TextButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.LifecycleStartEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.bob.android.ui.theme.BobBackground
import com.bob.android.ui.theme.BobBorder
import com.bob.android.ui.theme.BobButton
import com.bob.android.ui.theme.BobCard
import com.bob.android.ui.theme.BobInk
import com.bob.android.ui.theme.BobSecondaryInk
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch

@Composable
fun BobApp(viewModel: BobViewModel = viewModel()) {
    val imagePicker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(),
    ) { uris ->
        viewModel.endExternalContentSelection()
        viewModel.queueImages(uris)
    }
    val filePicker = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments(),
    ) { uris ->
        viewModel.endExternalContentSelection()
        viewModel.queueFiles(uris)
    }
    LifecycleStartEffect(viewModel) {
        viewModel.startDiscovery()
        onStopOrDispose { viewModel.stopDiscovery() }
    }

    val state by viewModel.uiState.collectAsStateWithLifecycle()
    BobScreen(
        state = state,
        onRefresh = viewModel::refresh,
        onSelectPeer = viewModel::selectPeer,
        onManualIpChanged = viewModel::updateManualIp,
        onUseManualIp = viewModel::useManualIp,
        onRequestConnection = viewModel::requestConnection,
        onDisconnect = viewModel::disconnect,
        onResetTrust = viewModel::resetTrust,
        onDraftChanged = viewModel::updateDraftText,
        onSendText = viewModel::sendText,
        onChooseImages = {
            viewModel.beginExternalContentSelection()
            imagePicker.launch(
                PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
            )
        },
        onChooseFiles = {
            viewModel.beginExternalContentSelection()
            filePicker.launch(arrayOf("*/*"))
        },
    )
}

private enum class HistoryCategory(val label: String) {
    Chat("Chat"), Text("Text"), Images("Images"), Files("Files"),
}

private data class ChatEntry(
    val key: String,
    val recordedAtMillis: Long,
    val text: TextTimelineItemUi? = null,
    val transfer: TransferTimelineItemUi? = null,
)

@Composable
private fun BobScreen(
    state: BobUiState,
    onRefresh: () -> Unit,
    onSelectPeer: (String) -> Unit,
    onManualIpChanged: (String) -> Unit,
    onUseManualIp: () -> Unit,
    onRequestConnection: () -> Unit,
    onDisconnect: () -> Unit,
    onResetTrust: () -> Unit,
    onDraftChanged: (String) -> Unit,
    onSendText: () -> Unit,
    onChooseImages: () -> Unit,
    onChooseFiles: () -> Unit,
) {
    var category by rememberSaveable { mutableStateOf(HistoryCategory.Chat) }
    var settingsExpanded by rememberSaveable { mutableStateOf(false) }
    var visibleCount by rememberSaveable(category) { mutableStateOf(40) }
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    var followingLatest by remember { mutableStateOf(true) }
    var previousLastKey by remember(category) { mutableStateOf<String?>(null) }
    val entries = remember(state.timeline, state.transfers, category) {
        buildList {
            if (category == HistoryCategory.Chat || category == HistoryCategory.Text) {
                state.timeline.forEach { add(ChatEntry("text:${it.key}", it.recordedAtMillis, text = it)) }
            }
            if (category != HistoryCategory.Text) {
                state.transfers.filter {
                    category == HistoryCategory.Chat ||
                        (category == HistoryCategory.Images && it.kindLabel == "Image") ||
                        (category == HistoryCategory.Files && it.kindLabel == "File")
                }.forEach { add(ChatEntry("transfer:${it.key}", it.recordedAtMillis, transfer = it)) }
            }
        }.sortedWith(compareBy(ChatEntry::recordedAtMillis, ChatEntry::key))
    }
    val visibleEntries = entries.takeLast(visibleCount)
    val hasEarlier = entries.size > visibleCount
    LaunchedEffect(listState) {
        snapshotFlow { !listState.canScrollForward }.collect { followingLatest = it }
    }
    LaunchedEffect(entries.lastOrNull()?.key, category) {
        if (visibleEntries.isNotEmpty() && (previousLastKey == null || followingLatest)) {
            listState.scrollToItem(visibleEntries.lastIndex + if (hasEarlier) 1 else 0)
        }
        previousLastKey = entries.lastOrNull()?.key
    }
    Column(
        modifier = Modifier.fillMaxSize().background(BobBackground)
            .windowInsetsPadding(WindowInsets.safeDrawing).imePadding(),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("BOB", color = BobInk, fontWeight = FontWeight.Black, fontSize = 26.sp)
            Column(Modifier.weight(1f)) {
                Text(
                    state.authenticatedPeerName ?: state.selectedTargetLabel ?: "Your computer",
                    color = BobInk, fontWeight = FontWeight.SemiBold, fontSize = 15.sp,
                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
                Text(state.connectionLabel, color = BobSecondaryInk, fontSize = 12.sp,
                    modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
            }
            TextButton(onClick = { settingsExpanded = !settingsExpanded }) {
                Text(if (settingsExpanded) "Done" else "Settings", color = BobInk)
            }
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            HistoryCategory.entries.forEach { tab ->
                TextButton(
                    onClick = { category = tab },
                    modifier = Modifier.weight(1f).selectable(
                        selected = category == tab, role = Role.Tab, onClick = { category = tab },
                    ).background(if (category == tab) BobButton else Color.Transparent, RoundedCornerShape(3.dp)),
                ) {
                    Text(tab.label, color = BobInk, fontWeight = if (category == tab) FontWeight.Bold else FontWeight.Normal)
                }
            }
        }
        if (settingsExpanded) {
            Column(Modifier.fillMaxWidth().heightIn(max = 300.dp).verticalScroll(rememberScrollState()).padding(16.dp)) {
                DeviceSection(state, onSelectPeer, onRefresh)
                Spacer(Modifier.height(10.dp))
                ManualAddressSection(state.manualIp, onManualIpChanged, onUseManualIp)
                Spacer(Modifier.height(10.dp))
                ConnectionSection(state, onRequestConnection, onDisconnect, onResetTrust)
                Spacer(Modifier.height(8.dp))
                Text("Received images: Pictures/BOB · Files: Download/BOB", color = BobSecondaryInk, fontSize = 12.sp)
            }
        }
        state.historyWarning?.let {
            Text(it, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp), color = BobInk, fontSize = 12.sp)
        }
        state.notice?.let {
            Text(it, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp), color = BobSecondaryInk, fontSize = 12.sp)
        }
        if (!settingsExpanded && state.connectionPhase != ConnectionUiPhase.Connected) {
            TextButton(onClick = { settingsExpanded = true }, modifier = Modifier.padding(horizontal = 8.dp)) {
                Text(if (state.isSearching) "Searching on this Wi-Fi · Connection settings" else "Choose a computer or enter its IP address", color = BobInk)
            }
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            if (hasEarlier) {
                item(key = "load-earlier") {
                    TextButton(onClick = {
                        val oldIndex = listState.firstVisibleItemIndex
                        val offset = listState.firstVisibleItemScrollOffset
                        val added = minOf(40, entries.size - visibleCount)
                        visibleCount += added
                        scope.launch { listState.scrollToItem(oldIndex + added - if (entries.size <= visibleCount) 1 else 0, offset) }
                    }, modifier = Modifier.fillMaxWidth()) {
                        Text("Load earlier records (${entries.size - visibleCount})", color = BobInk)
                    }
                }
            }
            if (visibleEntries.isEmpty()) {
                item { EmptyTimeline(state.connectionPhase == ConnectionUiPhase.Connected, category) }
            }
            items(visibleEntries, key = ChatEntry::key) { entry ->
                entry.text?.let { TimelineCard(it) }
                entry.transfer?.let { TransferCard(it) }
            }
        }
        if (!followingLatest && entries.isNotEmpty()) {
            TextButton(onClick = { scope.launch { listState.animateScrollToItem(visibleEntries.lastIndex + if (hasEarlier) 1 else 0) } }, modifier = Modifier.align(Alignment.End)) {
                Text("Latest messages ↓", color = BobInk)
            }
        }
        Box(Modifier.fillMaxWidth().background(BobBackground).border(1.dp, BobBorder).padding(horizontal = 16.dp, vertical = 10.dp)) {
            TextComposer(state, onDraftChanged, onSendText, onChooseImages, onChooseFiles)
        }
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
    address: String,
    onAddressChanged: (String) -> Unit,
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
            value = address,
            onValueChange = onAddressChanged,
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Windows IP address") },
            singleLine = true,
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
private fun EmptyTimeline(connected: Boolean, category: HistoryCategory) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(BobCard)
            .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
            .padding(horizontal = 14.dp, vertical = 22.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = when {
                !connected -> "Your saved records appear here. Connect to share something new."
                category == HistoryCategory.Images -> "No images yet. Choose Images below to send one."
                category == HistoryCategory.Files -> "No files yet. Choose Files below to send one."
                category == HistoryCategory.Text -> "No text messages yet. Write a message below."
                else -> "Connected. Send a message, image, or file."
            },
            color = BobSecondaryInk,
            fontSize = 14.sp,
        )
    }
}

@Composable
private fun TimelineCard(item: TextTimelineItemUi) {
    var overflow by remember(item.key) { mutableStateOf(false) }
    var expanded by rememberSaveable(item.key) { mutableStateOf(false) }
    Box(Modifier.fillMaxWidth(), contentAlignment = if (item.isOutgoing) Alignment.CenterEnd else Alignment.CenterStart) {
        Column(
            Modifier.fillMaxWidth(0.88f)
                .background(if (item.isOutgoing) BobButton else BobCard, RoundedCornerShape(4.dp))
                .border(1.dp, BobBorder, RoundedCornerShape(4.dp)).padding(12.dp),
        ) {
            if (expanded) {
                // A bounded native selection surface keeps the original string intact,
                // including selections across a maximum-size message's line boundaries.
                AndroidView(
                    modifier = Modifier.fillMaxWidth().heightIn(max = 420.dp),
                    factory = { context ->
                        ScrollView(context).apply {
                            addView(TextView(context).apply {
                                text = item.text
                                textSize = 16f
                                setTextColor(android.graphics.Color.rgb(25, 20, 31))
                                setTextIsSelectable(true)
                                setLineSpacing(0f, 1.3f)
                                setPadding(0, 0, 0, 0)
                            })
                        }
                    },
                )
            } else {
                SelectionContainer {
                    Text(
                        text = item.previewText, color = BobInk, fontSize = 16.sp, lineHeight = 23.sp,
                        maxLines = 8, overflow = TextOverflow.Ellipsis,
                        onTextLayout = { overflow = it.hasVisualOverflow },
                    )
                }
            }
            if (expanded || item.isTruncated || overflow) {
                TextButton(onClick = { expanded = !expanded }) {
                    Text(if (expanded) "Show less" else "Expand text", color = BobInk, fontSize = 13.sp)
                }
            }
            Spacer(Modifier.height(6.dp))
            Text("${item.timeLabel} · ${item.statusLabel}", color = BobSecondaryInk, fontSize = 11.sp,
                modifier = Modifier.align(Alignment.End))
        }
    }
}

@Composable
private fun TransferCard(item: TransferTimelineItemUi) {
    val context = LocalContext.current
    val previewUri = item.previewUri
    var showFullImage by rememberSaveable(item.key, previewUri) { mutableStateOf(false) }
    var showImageMenu by remember(item.key) { mutableStateOf(false) }
    val thumbnail = previewUri?.let { rememberContentBitmap(it, THUMBNAIL_MAX_PIXELS) }
    Box(Modifier.fillMaxWidth(), contentAlignment = if (item.isOutgoing) Alignment.CenterEnd else Alignment.CenterStart) {
        Column(
            Modifier.fillMaxWidth(0.88f)
                .background(if (item.isOutgoing) BobButton else BobCard, RoundedCornerShape(4.dp))
                .border(1.dp, BobBorder, RoundedCornerShape(4.dp))
                .then(if (item.kindLabel == "File" && item.statusLabel == "Completed" && item.contentUri != null) {
                    Modifier.clickable(role = Role.Button, onClickLabel = "Open file") {
                        openLocalFile(context, item.contentUri, item.mediaType)
                    }
                } else Modifier).padding(10.dp),
        ) {
            if (thumbnail != null) {
                Box {
                    Image(
                        bitmap = remember(thumbnail) { thumbnail.asImageBitmap() },
                        contentDescription = "${item.name}. Tap to view, touch and hold for image actions.",
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxWidth().heightIn(min = 100.dp, max = 240.dp)
                            .clip(RoundedCornerShape(3.dp)).background(BobBackground)
                            .combinedClickable(
                                role = Role.Button, onClickLabel = "View image",
                                onLongClickLabel = "Image actions",
                                onClick = { showFullImage = true }, onLongClick = { showImageMenu = true },
                            ),
                    )
                    DropdownMenu(expanded = showImageMenu, onDismissRequest = { showImageMenu = false }) {
                        DropdownMenuItem(text = { Text("Copy image") }, onClick = {
                            showImageMenu = false
                            copyImageToClipboard(context, item.name, previewUri)
                        })
                    }
                }
                Spacer(Modifier.height(8.dp))
            }
            Text(item.name, color = BobInk, fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
                maxLines = 2, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.height(4.dp))
            Text("${item.kindLabel} · ${item.progressLabel}", color = BobSecondaryInk, fontSize = 12.sp)
            item.errorMessage?.let {
                Spacer(Modifier.height(5.dp))
                Text(it, color = BobInk, fontSize = 12.sp, lineHeight = 18.sp)
            }
            if (previewUri != null && thumbnail == null) {
                Text("Image preview unavailable", color = BobSecondaryInk, fontSize = 12.sp)
            }
            Spacer(Modifier.height(6.dp))
            Text("${item.timeLabel} · ${item.statusLabel}", color = BobSecondaryInk, fontSize = 11.sp,
                modifier = Modifier.align(Alignment.End))
        }
    }
    if (showFullImage && thumbnail != null) {
        FullImageDialog(item.name, previewUri, thumbnail, onDismiss = { showFullImage = false })
    }
}

@Composable
private fun FullImageDialog(name: String, previewUri: String, thumbnail: Bitmap, onDismiss: () -> Unit) {
    val context = LocalContext.current
    var imageMenu by remember { mutableStateOf(false) }
    val fullImage = rememberContentBitmap(previewUri, FULL_IMAGE_MAX_PIXELS, thumbnail) ?: thumbnail
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Column(Modifier.fillMaxSize().padding(12.dp).background(BobCard)
            .border(1.dp, BobBorder, RoundedCornerShape(4.dp)).padding(12.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(name, color = BobInk, fontSize = 14.sp, maxLines = 2,
                    overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                TextButton(onClick = onDismiss) { Text("Close", color = BobInk) }
            }
            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                Image(
                    bitmap = remember(fullImage) { fullImage.asImageBitmap() },
                    contentDescription = "Image preview of $name. Touch and hold to copy.",
                    contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxSize().background(Color.Black)
                        .combinedClickable(onClick = {}, onLongClick = { imageMenu = true },
                            onLongClickLabel = "Image actions"),
                )
                DropdownMenu(expanded = imageMenu, onDismissRequest = { imageMenu = false }) {
                    DropdownMenuItem(text = { Text("Copy image") }, onClick = {
                        imageMenu = false
                        copyImageToClipboard(context, name, previewUri)
                    })
                }
            }
            Text("Touch and hold the image to copy", color = BobSecondaryInk, fontSize = 12.sp,
                modifier = Modifier.padding(top = 8.dp))
        }
    }
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
private fun TextComposer(
    state: BobUiState,
    onDraftChanged: (String) -> Unit,
    onSendText: () -> Unit,
    onChooseImages: () -> Unit,
    onChooseFiles: () -> Unit,
) {
    Column(Modifier.fillMaxWidth().heightIn(max = 220.dp).verticalScroll(rememberScrollState())) {
        OutlinedTextField(
            value = state.draftText,
            onValueChange = onDraftChanged,
            modifier = Modifier.fillMaxWidth(),
            placeholder = { Text("Message") },
            isError = state.draftError != null,
            minLines = 1,
            maxLines = 4,
            shape = RoundedCornerShape(3.dp),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = BobBorder, unfocusedBorderColor = BobBorder,
                focusedContainerColor = BobButton, unfocusedContainerColor = BobButton,
                cursorColor = BobInk,
            ),
        )
        state.draftError?.let { Text(it, color = BobInk, fontSize = 12.sp, modifier = Modifier.padding(top = 4.dp)) }
        Row(Modifier.fillMaxWidth().padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onChooseImages, enabled = state.canChooseContent) { Text("Images", color = BobInk) }
            TextButton(onClick = onChooseFiles, enabled = state.canChooseContent) { Text("Files", color = BobInk) }
            Spacer(Modifier.weight(1f))
            BobButton(
                label = if (state.isQueueingText) "Sending" else "Send",
                onClick = onSendText, enabled = state.canSendText,
            )
        }
    }
}

private fun openLocalFile(context: Context, uriValue: String, mediaType: String) {
    val opened = runCatching {
        val uri = Uri.parse(uriValue)
        require(uri.scheme == "content")
        context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { }
            ?: error("The file is no longer available.")
        context.startActivity(Intent(Intent.ACTION_VIEW)
            .setDataAndType(uri, mediaType)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
    }.isSuccess
    if (!opened) Toast.makeText(context, "The file is unavailable or no app can open it.", Toast.LENGTH_SHORT).show()
}
private fun copyImageToClipboard(context: Context, name: String, uriValue: String) {
    val copied = runCatching {
        val uri = Uri.parse(uriValue)
        require(uri.scheme == "content") { "Only local content URIs can be copied." }
        context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { }
            ?: error("The image is no longer available.")
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

private const val THUMBNAIL_MAX_PIXELS = 720
private const val FULL_IMAGE_MAX_PIXELS = 2_048

@Composable
private fun BobButton(
    label: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    fillWidth: Boolean = false,
) {
    Button(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier
            .then(if (fillWidth) Modifier.fillMaxWidth() else Modifier)
            .defaultMinSize(minHeight = 48.dp),
        shape = RoundedCornerShape(3.dp),
        border = BorderStroke(1.dp, BobBorder),
        colors = ButtonDefaults.buttonColors(
            containerColor = BobButton,
            contentColor = BobInk,
            disabledContainerColor = BobButton.copy(alpha = 0.55f),
            disabledContentColor = BobSecondaryInk.copy(alpha = 0.7f),
        ),
    ) {
        Text(text = label, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
    }
}
