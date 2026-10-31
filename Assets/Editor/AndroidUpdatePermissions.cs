#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

// Adds the permissions the in-app updater needs (Assets/Plugins/Android/RewiredApkInstaller.java)
// to the generated Android manifest:
// - REQUEST_INSTALL_PACKAGES lets Rewired install the APK it downloaded (Android asks the user
//   once to allow installs from Rewired).
// - UPDATE_PACKAGES_WITHOUT_USER_ACTION lets Android 12+ install Rewired's own updates without
//   showing the "Update this app?" dialog each time.
public class AndroidUpdatePermissions : IPostGenerateGradleAndroidProject
{
	const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

	static readonly string[] Permissions =
	{
		"android.permission.REQUEST_INSTALL_PACKAGES",
		"android.permission.UPDATE_PACKAGES_WITHOUT_USER_ACTION"
	};

	public int callbackOrder => 0;

	// path is the generated unityLibrary module.
	public void OnPostGenerateGradleAndroidProject(string path)
	{
		string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
		XmlDocument doc = new();
		doc.Load(manifestPath);
		XmlElement manifest = doc.DocumentElement;

		foreach (string permission in Permissions)
		{
			if (HasPermission(manifest, permission)) continue;
			XmlElement element = doc.CreateElement("uses-permission");
			XmlAttribute name = doc.CreateAttribute("android", "name", AndroidNamespace);
			name.Value = permission;
			element.Attributes.Append(name);
			manifest.PrependChild(element);
		}

		doc.Save(manifestPath);
	}

	static bool HasPermission(XmlElement manifest, string permission)
	{
		foreach (XmlNode node in manifest.ChildNodes)
		{
			if (node is XmlElement e && e.Name == "uses-permission" && e.GetAttribute("name", AndroidNamespace) == permission) return true;
		}

		return false;
	}
}
#endif
