// Unity keeps Application.persistentDataPath in an in-memory file system on WebGL.
// FS.syncfs(false) copies it to IndexedDB so projects survive a page reload.
mergeInto(LibraryManager.library, {
  RewiredSyncFS: function () {
    FS.syncfs(false, function (err) {
      if (err) console.warn("Rewired: could not save to browser storage", err);
    });
  }
});
