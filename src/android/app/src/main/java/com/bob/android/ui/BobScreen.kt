package com.bob.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.net.Uri
import android.os.CancellationSignal
import android.util.Size
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
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
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
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
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BobBackground)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .imePadding(),
    ) {
        LazyColumn(
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = 24.dp, vertical = 20.dp),
        ) {
        item {
            Header(discoveryLabel = state.discoveryLabel, active = state.isSearching)
            if (state.connectionPhase != ConnectionUiPhase.Connected) {
                Spacer(Modifier.height(40.dp))
                Text(
                    text = "Connect to your computer",
                    color = BobInk,
                    fontSize = 32.sp,
                    lineHeight = 36.sp,
                    fontWeight = FontWeight.Bold,
                )
                Spacer(Modifier.height(8.dp))
                Text(
                    text = "Your phone and Windows computer must be on the same Wi-Fi. The certificate pin is saved silently the first time.",
                    color = BobSecondaryInk,
                    fontSize = 16.sp,
                    lineHeight = 24.sp,
                )
                Spacer(Modifier.height(24.dp))
                DeviceSection(state, onSelectPeer, onRefresh)
                Spacer(Modifier.height(18.dp))
                ManualAddressSection(
                    address = state.manualIp,
                    onAddressChanged = onManualIpChanged,
                    onUseAddress = onUseManualIp,
                )
            }
            Spacer(Modifier.height(18.dp))
            ConnectionSection(
                state = state,
                onConnect = onRequestConnection,
                onDisconnect = onDisconnect,
                onResetTrust = onResetTrust,
            )
            state.notice?.let { message ->
                Spacer(Modifier.height(14.dp))
                Notice(message)
            }
            if (state.transfers.isNotEmpty()) {
                Spacer(Modifier.height(30.dp))
                Text(
                    text = "Transfers",
                    color = BobInk,
                    fontSize = 20.sp,
                    fontWeight = FontWeight.Bold,
                )
                Spacer(Modifier.height(10.dp))
            }
        }

        if (state.transfers.isNotEmpty()) {
            items(state.transfers, key = TransferTimelineItemUi::key) { item ->
                TransferCard(item)
                Spacer(Modifier.height(10.dp))
            }
        }

        item {
            Spacer(Modifier.height(30.dp))
            Text(
                text = "Recent messages · latest 20",
                color = BobInk,
                fontSize = 20.sp,
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(10.dp))
        }

        if (state.timeline.isEmpty()) {
            item { EmptyTimeline(state.connectionPhase == ConnectionUiPhase.Connected) }
        } else {
            items(state.timeline, key = TextTimelineItemUi::key) { item ->
                TimelineCard(item)
                Spacer(Modifier.height(10.dp))
            }
        }

        item {
            Spacer(Modifier.height(28.dp))
            Text(
                text = "BOB · Android HTTPS / TOFU / WSS",
                color = BobSecondaryInk,
                fontSize = 12.sp,
            )
        }
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .background(BobBackground)
                .border(1.dp, BobBorder)
                .padding(horizontal = 24.dp, vertical = 12.dp),
        ) {
            TextComposer(
                state = state,
                onDraftChanged = onDraftChanged,
                onSendText = onSendText,
                onChooseImages = onChooseImages,
                onChooseFiles = onChooseFiles,
            )
        }
    }
}

@Composable
private fun Header(discoveryLabel: String, active: Boolean) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = "BOB",
                color = BobInk,
                fontSize = 40.sp,
                lineHeight = 42.sp,
                fontWeight = FontWeight.Black,
            )
            Text(
                text = "Local file, image, and text transfer",
                color = BobSecondaryInk,
                fontSize = 14.sp,
            )
        }
        Spacer(Modifier.size(12.dp))
        Row(
            modifier = Modifier
                .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
                .padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(7.dp),
        ) {
            Box(
                Modifier
                    .size(8.dp)
                    .background(if (active) BobInk else BobSecondaryInk),
            )
            Text(
                text = discoveryLabel,
                color = BobInk,
                fontSize = 12.sp,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
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
private fun EmptyTimeline(connected: Boolean) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(BobCard)
            .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
            .padding(horizontal = 14.dp, vertical = 22.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = if (connected) "Securely connected. Send your first text." else "Connect to send text in both directions.",
            color = BobSecondaryInk,
            fontSize = 14.sp,
        )
    }
}

@Composable
private fun TimelineCard(item: TextTimelineItemUi) {
    val context = LocalContext.current
    var previewOverflows by remember(item.key) { mutableStateOf(false) }
    var showFullText by remember(item.key) { mutableStateOf(false) }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(if (item.isOutgoing) BobButton else BobCard)
            .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
            .padding(14.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(
                text = "${item.directionLabel} · ${item.statusLabel}",
                color = BobInk,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
            )
            Text(text = item.timeLabel, color = BobSecondaryInk, fontSize = 12.sp)
        }
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
        if (item.isTruncated || previewOverflows) {
            Spacer(Modifier.height(10.dp))
            BobButton(
                label = "View full text",
                onClick = { showFullText = true },
            )
        }
        Spacer(Modifier.height(10.dp))
        BobButton(
            label = "Copy full text",
            onClick = { copyTextToClipboard(context, item.text) },
        )
    }
    if (showFullText) {
        FullTextDialog(
            text = item.text,
            onDismiss = { showFullText = false },
        )
    }
}

@Composable
private fun TransferCard(item: TransferTimelineItemUi) {
    val context = LocalContext.current
    val previewUri = item.previewUri
    var showFullImage by rememberSaveable(item.key, item.previewUri) { mutableStateOf(false) }
    val thumbnail = previewUri?.let { uri ->
        rememberContentBitmap(uri, THUMBNAIL_MAX_PIXELS)
    }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(BobCard)
            .border(1.dp, BobBorder, RoundedCornerShape(3.dp))
            .padding(14.dp),
    ) {
        Text(
            text = item.name,
            color = BobInk,
            fontSize = 16.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = "${item.directionLabel} ${item.kindLabel.lowercase()} · ${item.statusLabel}",
            color = BobInk,
            fontSize = 13.sp,
        )
        Spacer(Modifier.height(4.dp))
        Text(text = item.progressLabel, color = BobSecondaryInk, fontSize = 13.sp)
        if (thumbnail != null) {
            Spacer(Modifier.height(10.dp))
            Image(
                bitmap = remember(thumbnail) { thumbnail.asImageBitmap() },
                contentDescription = "Preview of ${item.name}",
                contentScale = ContentScale.Crop,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(180.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(BobButton)
                    .clickable(
                        role = Role.Button,
                        onClickLabel = "View full image",
                        onClick = { showFullImage = true },
                    )
                    .border(1.dp, BobBorder, RoundedCornerShape(3.dp)),
            )
            Spacer(Modifier.height(8.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                BobButton(
                    label = "View image",
                    onClick = { showFullImage = true },
                    modifier = Modifier.weight(1f),
                )
                BobButton(
                    label = "Copy image",
                    onClick = { copyImageToClipboard(context, item.name, previewUri) },
                    modifier = Modifier.weight(1f),
                )
            }
        } else if (previewUri != null) {
            Spacer(Modifier.height(8.dp))
            BobButton(
                label = "Copy image",
                onClick = { copyImageToClipboard(context, item.name, previewUri) },
                fillWidth = true,
            )
        }
        item.errorMessage?.let { error ->
            Spacer(Modifier.height(6.dp))
            Text(text = error, color = BobInk, fontSize = 13.sp, lineHeight = 18.sp)
        }
    }
    if (showFullImage && thumbnail != null) {
        FullImageDialog(
            name = item.name,
            previewUri = item.previewUri,
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
    onDraftChanged: (String) -> Unit,
    onSendText: () -> Unit,
    onChooseImages: () -> Unit,
    onChooseFiles: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(max = 260.dp)
            .verticalScroll(rememberScrollState()),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            BobButton(
                label = "Choose images",
                onClick = onChooseImages,
                modifier = Modifier.weight(1f),
                enabled = state.canChooseContent,
            )
            BobButton(
                label = "Choose files",
                onClick = onChooseFiles,
                modifier = Modifier.weight(1f),
                enabled = state.canChooseContent,
            )
        }
        Spacer(Modifier.height(10.dp))
        OutlinedTextField(
            value = state.draftText,
            onValueChange = onDraftChanged,
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Send text") },
            placeholder = { Text("Enter or paste text. It will be sent unchanged.") },
            supportingText = {
                Text(
                    state.draftError ?: if (state.connectionPhase == ConnectionUiPhase.Connected) {
                        "Text is saved first, then sent over WSS and held until the computer acknowledges it."
                    } else {
                        "Connect securely to enable sending."
                    },
                    modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                )
            },
            isError = state.draftError != null,
            minLines = 2,
            maxLines = 5,
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
        BobButton(
            label = if (state.isQueueingText) "Queueing" else "Send text",
            onClick = onSendText,
            enabled = state.canSendText,
            fillWidth = true,
        )
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
