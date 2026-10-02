package app.sonora.receiver

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioManager
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import androidx.compose.runtime.mutableStateOf
import app.sonora.receiver.net.AudioReceiver
import app.sonora.receiver.net.PairingLink
import app.sonora.receiver.net.PcFinder

// Keeps playback alive with the screen off: a mediaPlayback foreground service. It keeps listening
// to the PC (over the cable, or on Wi‑Fi), so a stream the PC ends stops here too, and it holds a
// media session so headphone buttons control the PC's playing app. Over Wi‑Fi it also holds a
// low-latency Wi‑Fi lock so power saving doesn't add jitter, and the audio arrives as UDP.
class ReceiverService : Service() {
    private var wifiLock: WifiManager.WifiLock? = null
    private var watching = false
    private var sawMine = false
    private var generation = 0
    private var overWifi = false
    private var session: MediaSession? = null
    private var computer = "your PC"
    private val onMedia: () -> Unit = { publishSession() }

    // The PC ended this stream (its Disconnect or Quit, or it restarted and lost the key), or
    // couldn't start it: stop too, rather than wait for audio that won't come.
    private val onPc: (PcFinder.Pc) -> Unit = { pc ->
        if (pc.mine) sawMine = true
        else if (sawMine || pc.problem != null) stopSelf()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { endOnPc(); stopSelf(); return START_NOT_STICKY }
        val secret = intent?.getByteArrayExtra(EXTRA_SECRET)
        if (secret == null || secret.size != 32) { stopSelf(); return START_NOT_STICKY }
        computer = intent.getStringExtra(EXTRA_COMPUTER) ?: "your PC"
        val host = intent.getStringExtra(EXTRA_HOST)
        val port = intent.getIntExtra(EXTRA_PORT, DEFAULT_PORT)
        generation = started

        val notification = notification(computer)
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
        else startForeground(NOTIFICATION_ID, notification)

        // The PC's presence channel follows the stream: over the adb tunnel, or to the PC on Wi‑Fi.
        overWifi = host != LOOPBACK
        PcFinder.phoneId = Trust.phoneId(this)
        val audio = getSystemService(AudioManager::class.java)
        PcFinder.mediaVolume = {
            val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
            audio.getStreamVolume(AudioManager.STREAM_MUSIC) * 100 / max
        }
        sawMine = PcFinder.pc?.mine == true
        if (!watching) { watching = true; PcFinder.hold(this); PcFinder.listen(onPc) }
        // Lets the PC show this phone what's playing and take its media buttons.
        PcFinder.authorize(secret)
        startSession()
        if (overWifi && wifiLock == null) {
            val wifi = applicationContext.getSystemService(WifiManager::class.java)
            @Suppress("DEPRECATION")
            val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifiLock = wifi.createWifiLock(mode, "Sonora:stream").apply { acquire() }
        }

        current?.stop()
        // USB: TCP through the tunnel. Wi‑Fi: the PC sends UDP to this port.
        current = AudioReceiver(secret, if (overWifi) null else host, port).also {
            it.preferred = Outputs.device(this, Outputs.chosen)
            it.start()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        endSession()
        if (watching) { PcFinder.unlisten(onPc); PcFinder.release(this) }
        current?.stop()
        current = null
        // A stream started since this one was stopped keeps its place.
        if (generation == started) {
            active.value = null
            PcFinder.authorize(null)
            // The channel goes back to the cable once a Wi‑Fi stream ends.
            if (overWifi) PcFinder.useWifi(null, null)
        }
        wifiLock?.takeIf { it.isHeld }?.release()
        super.onDestroy()
    }

    // The system's media controls for this stream. Android sends media keys (a Bluetooth or wired
    // headset's play/pause, next and previous, a double-press of a wired headset's button, a car's
    // controls) to the session of the app playing audio, which while this streams is this one;
    // they go to the PC's playing app. Play and pause are sent as such, never as a toggle, so a
    // press can't flip the wrong way. The session also carries the track, for headsets and cars
    // that show it.
    private fun startSession() {
        if (session == null) {
            session = MediaSession(this, "Sonora").apply {
                setCallback(object : MediaSession.Callback() {
                    override fun onPlay() = PcFinder.control("play")
                    override fun onPause() = PcFinder.control("pause")
                    override fun onSkipToNext() = PcFinder.control("next")
                    override fun onSkipToPrevious() = PcFinder.control("previous")
                    override fun onSeekTo(pos: Long) = PcFinder.control("seek", pos)
                }, Handler(Looper.getMainLooper()))
                isActive = true
            }
            PcFinder.listenMedia(onMedia)
        }
        publishSession()
    }

    private fun endSession() {
        PcFinder.unlistenMedia(onMedia)
        session?.run { isActive = false; release() }
        session = null
    }

    // What the PC says is playing, and which of its buttons the playing app accepts: a key the app
    // can't take isn't offered, so it does nothing rather than something wrong.
    private fun publishSession() {
        val s = session ?: return
        val media = PcFinder.media
        val metadata = MediaMetadata.Builder()
            .putString(MediaMetadata.METADATA_KEY_TITLE, media?.title?.takeIf { it.isNotBlank() } ?: "Desktop audio")
            .putString(MediaMetadata.METADATA_KEY_ARTIST, media?.artist?.takeIf { it.isNotBlank() } ?: "Playing from $computer")
            .putString(MediaMetadata.METADATA_KEY_ALBUM, media?.app.orEmpty())
        if (media != null && media.durationMs > 0) metadata.putLong(MediaMetadata.METADATA_KEY_DURATION, media.durationMs)
        PcFinder.artwork?.let { metadata.putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, it) }
        s.setMetadata(metadata.build())

        var actions = 0L
        if (media?.canToggle == true) actions = actions or PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE
        if (media?.canNext == true) actions = actions or PlaybackState.ACTION_SKIP_TO_NEXT
        if (media?.canPrevious == true) actions = actions or PlaybackState.ACTION_SKIP_TO_PREVIOUS
        if (media?.canSeek == true) actions = actions or PlaybackState.ACTION_SEEK_TO
        val now = SystemClock.elapsedRealtime()
        val playing = media?.playing == true
        s.setPlaybackState(
            PlaybackState.Builder()
                .setActions(actions)
                .setState(
                    if (playing) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED,
                    media?.position(now) ?: PlaybackState.PLAYBACK_POSITION_UNKNOWN,
                    if (playing) 1f else 0f,
                    now,
                )
                .build(),
        )
    }

    private fun endOnPc() {
        active.value?.let { PcFinder.end(it.secret) }
    }

    private fun notification(computer: String): Notification {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Playback", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, ReceiverService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("Playing from $computer")
            .setContentText("Sonora")
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Disconnect", stop).build())
            .setOngoing(true)
            .build()
    }

    companion object {
        const val EXTRA_SECRET = "secret"
        const val EXTRA_COMPUTER = "computer"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val DEFAULT_PORT = 47_210
        const val LOOPBACK = "127.0.0.1"
        private const val ACTION_STOP = "app.sonora.receiver.STOP"
        private const val CHANNEL = "playback"
        private const val NOTIFICATION_ID = 1

        // The running receiver, for the UI's controls and live stats.
        @Volatile var current: AudioReceiver? = null
            private set

        // What the running receiver is connected to. Compose state, so every screen follows it
        // when the notification or the PC ends the stream.
        private val active = mutableStateOf<PairingLink?>(null)
        val activeLink: PairingLink? get() = active.value
        private var started = 0

        fun start(context: Context, link: PairingLink) {
            started++
            active.value = link
            context.startForegroundService(
                Intent(context, ReceiverService::class.java)
                    .putExtra(EXTRA_SECRET, link.secret)
                    .putExtra(EXTRA_COMPUTER, link.name)
                    .putExtra(EXTRA_HOST, link.host)
                    .putExtra(EXTRA_PORT, link.port),
            )
        }

        // The listener's Disconnect, in the app or the notification: ends it on the PC as well.
        fun disconnect(context: Context) {
            active.value?.let { PcFinder.end(it.secret) }
            context.stopService(Intent(context, ReceiverService::class.java))
        }
    }
}
