package app.sonora.receiver

import android.content.Context
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

// The phone's outputs that can play a media stream, named the way people know them.
object Outputs {
    enum class Kind { Speaker, Wired, Usb, Bluetooth, Other }

    // The output picked in the player, by AudioDeviceInfo id; null lets Android choose (usually
    // the newest headphones). Kept while the app runs, not saved: a pick for tonight's headphones
    // shouldn't surprise anyone tomorrow. An id stops matching once that device disconnects.
    var chosen by mutableStateOf<Int?>(null)

    fun available(context: Context): List<AudioDeviceInfo> {
        val audio = context.getSystemService(AudioManager::class.java)
        val seen = mutableSetOf<String>()
        return audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
            .filter { it.isSink && kindOf(it) != null }
            .sortedBy { kindOf(it)!!.ordinal }
            // A headset can appear twice (Bluetooth classic and LE Audio); list it once.
            .filter { seen.add(kindOf(it)!!.name + name(it)) }
    }

    fun device(context: Context, id: Int?): AudioDeviceInfo? =
        if (id == null) null else available(context).firstOrNull { it.id == id }

    fun kind(device: AudioDeviceInfo): Kind = kindOf(device) ?: Kind.Other

    fun name(device: AudioDeviceInfo): String = when (device.type) {
        AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> "Phone speaker"
        AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_WIRED_HEADPHONES -> "Wired headphones"
        AudioDeviceInfo.TYPE_LINE_ANALOG, AudioDeviceInfo.TYPE_AUX_LINE -> "Line out"
        AudioDeviceInfo.TYPE_HDMI -> "HDMI"
        AudioDeviceInfo.TYPE_DOCK -> "Dock"
        else -> device.productName?.toString()?.trim()?.takeIf { it.isNotEmpty() && it != Build.MODEL }
            ?: when (kind(device)) {
                Kind.Usb -> "USB audio"
                Kind.Bluetooth -> "Bluetooth"
                else -> "Audio output"
            }
    }

    private fun kindOf(device: AudioDeviceInfo): Kind? = when (device.type) {
        AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> Kind.Speaker
        AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_WIRED_HEADPHONES,
        AudioDeviceInfo.TYPE_LINE_ANALOG, AudioDeviceInfo.TYPE_AUX_LINE -> Kind.Wired
        AudioDeviceInfo.TYPE_USB_HEADSET, AudioDeviceInfo.TYPE_USB_DEVICE -> Kind.Usb
        AudioDeviceInfo.TYPE_BLUETOOTH_A2DP, AudioDeviceInfo.TYPE_HEARING_AID,
        AudioDeviceInfo.TYPE_BLE_HEADSET, AudioDeviceInfo.TYPE_BLE_SPEAKER -> Kind.Bluetooth
        AudioDeviceInfo.TYPE_HDMI, AudioDeviceInfo.TYPE_DOCK -> Kind.Other
        // Earpiece, call and system-only routes can't carry a music stream.
        else -> null
    }
}
