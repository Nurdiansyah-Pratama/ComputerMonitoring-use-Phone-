package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.content.pm.PackageManager
import android.os.Build
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Listens for Android notifications on this phone so PhoneBox can display them cleanly below the weather widget.
 * Requires the standard user-granted Notification Access permission (no root).
 */
class PhoneNotifListener : NotificationListenerService() {

    data class Item(
        val key: String,
        val pkg: String,
        val app: String,
        val title: String,
        val text: String,
        val at: Long,
    ) {
        fun json(): JSONObject = JSONObject()
            .put("key", key)
            .put("pkg", pkg)
            .put("app", app)
            .put("title", title)
            .put("text", text)
            .put("at", at)
    }

    override fun onListenerConnected() {
        super.onListenerConnected()
        instance = this
        syncActive()
    }

    override fun onListenerDisconnected() {
        if (instance === this) instance = null
        super.onListenerDisconnected()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn == null) return
        val item = parse(sbn) ?: return
        upsert(item)
        listener?.invoke("post", item.json().toString())
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification?) {
        if (sbn == null) return
        val key = sbn.key ?: return
        if (removeByKey(key)) {
            listener?.invoke("remove", JSONObject().put("key", key).toString())
        }
    }

    private fun syncActive() {
        try {
            val active = activeNotifications ?: return
            val parsed = active.mapNotNull { parse(it) }
                .sortedByDescending { it.at }
                .take(MAX_ITEMS)
            items.clear()
            items.addAll(parsed)
            listener?.invoke("sync", snapshotJson())
        } catch (_: Exception) {}
    }

    private fun parse(sbn: StatusBarNotification): Item? {
        if (sbn.packageName == packageName) return null
        if (sbn.isOngoing) return null
        val n = sbn.notification ?: return null
        if ((n.flags and Notification.FLAG_FOREGROUND_SERVICE) != 0) return null
        if ((n.flags and Notification.FLAG_GROUP_SUMMARY) != 0) return null
        if ((n.flags and Notification.FLAG_ONGOING_EVENT) != 0) return null
        if (n.category == Notification.CATEGORY_TRANSPORT || n.category == Notification.CATEGORY_SERVICE) return null

        val extras = n.extras ?: return null
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim().orEmpty()
        val bigText = extras.getCharSequence(Notification.EXTRA_BIG_TEXT)?.toString()?.trim().orEmpty()
        val shortText = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString()?.trim().orEmpty()
        val text = (if (bigText.isNotEmpty()) bigText else shortText).replace('\n', ' ').take(180)
        val cleanTitle = title.replace('\n', ' ').take(80)
        if (cleanTitle.isEmpty() && text.isEmpty()) return null

        val appName = resolveAppName(sbn.packageName)
        return Item(
            key = sbn.key ?: "${sbn.packageName}:${sbn.id}",
            pkg = sbn.packageName,
            app = appName,
            title = cleanTitle,
            text = text,
            at = if (sbn.postTime > 0) sbn.postTime else System.currentTimeMillis(),
        )
    }

    private fun resolveAppName(pkg: String): String = try {
        val pm = packageManager
        val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            pm.getApplicationInfo(pkg, PackageManager.ApplicationInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            pm.getApplicationInfo(pkg, 0)
        }
        pm.getApplicationLabel(info).toString().take(40)
    } catch (_: Exception) {
        pkg.substringAfterLast('.')
    }

    companion object {
        private const val MAX_ITEMS = 10
        @Volatile private var instance: PhoneNotifListener? = null
        @Volatile var listener: ((action: String, payloadJson: String) -> Unit)? = null
        private val items = CopyOnWriteArrayList<Item>()

        private fun upsert(item: Item) {
            items.removeAll { it.key == item.key }
            items.add(0, item)
            while (items.size > MAX_ITEMS) items.removeAt(items.size - 1)
        }

        private fun removeByKey(key: String): Boolean {
            val before = items.size
            items.removeAll { it.key == key }
            return items.size != before
        }

        fun snapshotJson(): String = JSONArray().apply {
            items.forEach { put(it.json()) }
        }.toString()

        fun dismiss(key: String) {
            removeByKey(key)
            try {
                instance?.cancelNotification(key)
            } catch (_: Exception) {}
        }
    }
}
