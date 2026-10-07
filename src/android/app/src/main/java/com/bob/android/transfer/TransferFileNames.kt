package com.bob.android.transfer

import java.text.Normalizer
import java.util.Locale

internal fun sanitizeBobFileName(raw: String): String {
    val normalized = Normalizer.normalize(raw, Normalizer.Form.NFC)
    val cleaned = buildString(normalized.length) {
        normalized.codePoints().forEach { point ->
            val invalid = Character.isISOControl(point) ||
                (point <= 0xFFFF && point.toChar() in "/\\<>:\"|?*")
            if (!invalid) appendCodePoint(point)
        }
    }.trimEnd(' ', '.')
    var name = cleaned.ifBlank { "unnamed" }
    val base = name.substringBefore('.').trimEnd(' ', '.').uppercase(Locale.ROOT)
    if (base in WINDOWS_RESERVED_NAMES || base.matches(Regex("COM[1-9]|LPT[1-9]"))) name = "_" + name
    if (name.toByteArray(Charsets.UTF_8).size <= 200) return name
    val dot = name.lastIndexOf('.').takeIf { it > 0 }
    val extension = dot?.let { name.substring(it) }
        ?.takeIf { it.toByteArray(Charsets.UTF_8).size <= 192 }.orEmpty()
    val stem = if (extension.isEmpty()) name else name.substring(0, requireNotNull(dot))
    return stem.truncateUtf8(200 - extension.toByteArray(Charsets.UTF_8).size) + extension
}

internal fun String.withCollisionSuffix(suffix: Int): String {
    val dot = lastIndexOf('.').takeIf { it > 0 }
    val base = if (dot == null) this else substring(0, dot)
    val extension = if (dot == null) "" else substring(dot)
    val marker = " (" + suffix + ")"
    val maxBaseBytes = (200 - marker.toByteArray(Charsets.UTF_8).size -
        extension.toByteArray(Charsets.UTF_8).size).coerceAtLeast(1)
    return (base.truncateUtf8(maxBaseBytes) + marker + extension).truncateUtf8(200)
}

internal fun String.truncateUtf8(maxBytes: Int): String {
    if (toByteArray(Charsets.UTF_8).size <= maxBytes) return this
    val output = StringBuilder()
    var used = 0
    var offset = 0
    while (offset < length) {
        val point = codePointAt(offset)
        val text = String(Character.toChars(point))
        val bytes = text.toByteArray(Charsets.UTF_8).size
        if (used + bytes > maxBytes) break
        output.append(text)
        used += bytes
        offset += Character.charCount(point)
    }
    return output.toString()
}

private val WINDOWS_RESERVED_NAMES = setOf("CON", "PRN", "AUX", "NUL")
