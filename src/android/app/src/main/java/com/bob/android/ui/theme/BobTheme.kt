package com.bob.android.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val BobBackground = Color(0xFFCBA6F7)
val BobInk = Color(0xFF19141F)
val BobSecondaryInk = Color(0xFF4B4054)
val BobBorder = Color(0xFF32263B)
val BobCard = Color(0xFFD8B7FA)
val BobButton = Color(0xFFF7F1FC)

private val BobColors = lightColorScheme(
    primary = BobInk,
    onPrimary = BobButton,
    secondary = BobSecondaryInk,
    onSecondary = BobButton,
    background = BobBackground,
    onBackground = BobInk,
    surface = BobCard,
    onSurface = BobInk,
    surfaceVariant = BobButton,
    onSurfaceVariant = BobSecondaryInk,
    outline = BobBorder,
    error = Color(0xFF8E2230),
    onError = Color.White,
)

@Composable
fun BobTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = BobColors,
        typography = Typography(),
        content = content,
    )
}
