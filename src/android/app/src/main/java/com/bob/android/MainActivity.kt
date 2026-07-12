package com.bob.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.bob.android.ui.BobApp
import com.bob.android.ui.theme.BobTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val background = 0xFFCBA6F7.toInt()
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.light(background, background),
            navigationBarStyle = SystemBarStyle.light(background, background),
        )
        setContent {
            BobTheme {
                BobApp()
            }
        }
    }
}
