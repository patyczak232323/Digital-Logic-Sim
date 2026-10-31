package com.rewired.updater;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInstaller;
import android.os.Build;
import android.app.PendingIntent;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

// Installs a downloaded Rewired APK over the running app with Android's PackageInstaller,
// called from AppUpdater.cs. On Android 12 and newer an app may update itself without a
// confirmation dialog once the user has allowed it to install apps (asked the first time);
// otherwise Android shows its usual "Update this app?" dialog, which this class opens itself.
// A successful install stops this process, so the "installed" status is rarely seen.
//
// getStatus() returns one of: "" (nothing yet), "copying", "committed", "confirm" (Android's
// dialog is open), "installed", or "failed:<message>".
public final class RewiredApkInstaller {
	static final String ACTION = "com.rewired.updater.INSTALL_STATUS";

	static volatile String status = "";
	static BroadcastReceiver receiver;

	private RewiredApkInstaller() {}

	public static String getStatus() {
		return status;
	}

	public static void install(final Activity activity, final String apkPath) {
		status = "copying";
		final Context context = activity.getApplicationContext();
		new Thread(new Runnable() {
			@Override
			public void run() {
				try {
					installNow(context, apkPath);
				} catch (Throwable e) {
					status = "failed:" + describe(e);
				}
			}
		}, "RewiredUpdateInstall").start();
	}

	static void installNow(Context context, String apkPath) throws Exception {
		File apk = new File(apkPath);
		PackageInstaller installer = context.getPackageManager().getPackageInstaller();
		PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
		params.setAppPackageName(context.getPackageName());
		params.setSize(apk.length());
		if (Build.VERSION.SDK_INT >= 31) {
			params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
		}

		int sessionId = installer.createSession(params);
		PackageInstaller.Session session = installer.openSession(sessionId);
		try {
			try (InputStream in = new FileInputStream(apk); OutputStream out = session.openWrite("rewired.apk", 0, apk.length())) {
				byte[] buffer = new byte[1 << 16];
				int read;
				while ((read = in.read(buffer)) != -1) out.write(buffer, 0, read);
				session.fsync(out);
			}

			registerReceiver(context);
			Intent result = new Intent(ACTION).setPackage(context.getPackageName());
			// Mutable: PackageInstaller adds the status to this intent.
			int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
			PendingIntent pending = PendingIntent.getBroadcast(context, sessionId, result, flags);
			status = "committed";
			session.commit(pending.getIntentSender());
		} catch (Exception e) {
			session.abandon();
			throw e;
		} finally {
			session.close();
		}
	}

	static synchronized void registerReceiver(Context context) {
		if (receiver != null) return;
		receiver = new BroadcastReceiver() {
			@Override
			public void onReceive(Context c, Intent intent) {
				onStatus(c, intent);
			}
		};
		IntentFilter filter = new IntentFilter(ACTION);
		if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
		else context.registerReceiver(receiver, filter);
	}

	@SuppressWarnings("deprecation")
	static void onStatus(Context context, Intent intent) {
		int code = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
		switch (code) {
			case PackageInstaller.STATUS_PENDING_USER_ACTION:
				// Android wants the user to confirm (first update, older Android, or installs from
				// unknown apps not allowed yet): open its dialog.
				Intent confirm = intent.getParcelableExtra(Intent.EXTRA_INTENT);
				if (confirm == null) {
					status = "failed:Android did not show the install dialog";
					return;
				}
				confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
				try {
					context.startActivity(confirm);
					status = "confirm";
				} catch (Throwable e) {
					status = "failed:" + describe(e);
				}
				break;
			case PackageInstaller.STATUS_SUCCESS:
				status = "installed";
				break;
			case PackageInstaller.STATUS_FAILURE_ABORTED:
				status = "failed:the update was cancelled";
				break;
			default:
				String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
				status = "failed:" + (message == null || message.isEmpty() ? "Android could not install the update (code " + code + ")" : message);
				break;
		}
	}

	static String describe(Throwable e) {
		String message = e.getMessage();
		return message == null || message.isEmpty() ? e.getClass().getSimpleName() : message;
	}
}
