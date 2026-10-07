package com.bob.android

import android.os.Bundle
import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.bob.android.background.BobTransferRuntime
import com.bob.android.persistence.TextStoreException
import com.bob.android.ui.BobApp
import com.bob.android.ui.theme.BobTheme

class MainActivity : ComponentActivity() {
    private var storageUnavailable by mutableStateOf(false)
    private val notificationPermission = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val background = 0xFFCBA6F7.toInt()
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(background, background),
            navigationBarStyle = SystemBarStyle.light(background, background),
        )
        restoreRuntime()
        setContent {
            BobTheme {
                if (storageUnavailable) {
                    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)
                        .safeDrawingPadding().padding(24.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
                        Text("BOB", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Black,
                            color = MaterialTheme.colorScheme.onBackground)
                        Text("Saved history is unavailable", style = MaterialTheme.typography.titleLarge,
                            color = MaterialTheme.colorScheme.onBackground)
                        Text("BOB could not safely read its saved history. The original data was kept. Restore storage or a backup, then retry.",
                            color = MaterialTheme.colorScheme.onBackground)
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            OutlinedButton(onClick = ::finish, shape = RoundedCornerShape(4.dp)) { Text("Close") }
                            Button(onClick = ::restoreRuntime, shape = RoundedCornerShape(4.dp),
                                colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.surfaceVariant,
                                    contentColor = MaterialTheme.colorScheme.onBackground)) { Text("Retry") }
                        }
                    }
                } else {
                    BobApp()
                }
            }
        }
        if (!storageUnavailable && savedInstanceState == null &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    private fun restoreRuntime() {
        storageUnavailable = try {
            BobTransferRuntime.get(this)
            false
        } catch (_: TextStoreException) {
            true
        }
    }
}
