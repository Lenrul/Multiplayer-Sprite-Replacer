using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Msr
{
    /// <summary>Downloads a skin .zip from a URL and installs it as CustomSprites/&lt;name&gt;/...</summary>
    internal static class Downloader
    {
        private const long MaxDownloadBytes = 64L * 1024 * 1024;
        private const long MaxExtractedBytes = 256L * 1024 * 1024;
        private const int MaxEntries = 4000;
        private static readonly string[] CategoryFolders = { "body", "head", "woundview", "minigame" };

        public static bool Busy { get; private set; }

        /// <param name="report">(message, finished) - called on the main thread.</param>
        public static void Start(string url, Action<string, bool> report)
        {
            if (Busy) { report("A download is already running.", false); return; }
            url = CleanUrl(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                report("That is not a http(s) URL.", true);
                return;
            }
            Busy = true;
            Plugin.Runner.Run(Run(uri, report));
        }

        /// <summary>
        /// Pulls the link out of whatever was pasted. The skin site's copy button yields
        /// "skin load-remote https://skin.cat-bot.de/d/968"; only the link matters.
        /// </summary>
        internal static string CleanUrl(string pasted)
        {
            string s = (pasted ?? "").Trim().Trim('"', (char)39);
            int at = s.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            int ats = s.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (ats >= 0 && (at < 0 || ats < at)) at = ats;
            if (at > 0) s = s.Substring(at);
            int end = 0;
            while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
            s = s.Substring(0, end);
            return s.Trim('"', (char)39);
        }

        private static IEnumerator Run(Uri uri, Action<string, bool> report)
        {
            report("Downloading...", false);
            byte[] data = null;
            string error = null, headerName = null;
            using (var req = UnityWebRequest.Get(uri))
            {
                req.timeout = 120;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if ((long)req.downloadedBytes > MaxDownloadBytes)
                    {
                        req.Abort();
                        error = "File is larger than " + MaxDownloadBytes / (1024 * 1024) + " MB.";
                        break;
                    }
                    if (req.downloadedBytes > 0) report("Downloading... " + req.downloadedBytes / 1024 + " KB", false);
                    yield return null;
                }
                if (error == null)
                {
                    if (req.result != UnityWebRequest.Result.Success) error = "Download failed: " + req.error;
                    else
                    {
                        data = req.downloadHandler.data;
                        headerName = FileNameFromHeader(req.GetResponseHeader("Content-Disposition"));
                    }
                }
            }

            if (error == null)
            {
                try
                {
                    string suggested = headerName ?? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                    var installed = Install(data, suggested, uri.ToString());
                    SkinLibrary.Rescan();
                    report("Installed: " + string.Join(", ", installed), true);
                }
                catch (Exception e)
                {
                    error = e is InvalidDataException ? e.Message : "Install failed: " + e.Message;
                    Plugin.Log.LogWarning("Skin download from " + uri + " failed: " + e);
                }
            }
            Busy = false;
            if (error != null) report(error, true);
        }

        private static string FileNameFromHeader(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            foreach (var part in header.Split(';'))
            {
                var p = part.Trim();
                if (!p.StartsWith("filename", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = p.IndexOf('=');
                if (eq < 0) continue;
                string v = p.Substring(eq + 1).Trim().Trim('"');
                int tick = v.LastIndexOf('\'');           // filename*=UTF-8''name.zip
                if (tick >= 0) v = Uri.UnescapeDataString(v.Substring(tick + 1));
                if (v.Length > 0) return v;
            }
            return null;
        }

        /// <summary>Unpacks a downloaded archive into one or more skin folders. Returns their names.</summary>
        internal static List<string> Install(byte[] zip, string suggestedName, string sourceUrl)
        {
            if (zip == null || zip.Length < 4 || zip[0] != (byte)'P' || zip[1] != (byte)'K')
                throw new InvalidDataException("The link did not return a .zip file.");

            string root = SkinLibrary.Root;
            Directory.CreateDirectory(root);
            string tmp = Path.Combine(root, ".download_tmp");
            SafeDelete(tmp);
            Directory.CreateDirectory(tmp);
            try
            {
                long total = 0;
                int pngs = 0, count = 0;
                using (var ms = new MemoryStream(zip))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (++count > MaxEntries) throw new InvalidDataException("The archive has too many files.");
                        string rel = SafeRelative(entry.FullName);
                        if (rel == null || rel.EndsWith("/")) continue;
                        string ext = Path.GetExtension(rel).ToLowerInvariant();
                        string file = Path.GetFileName(rel);
                        bool isMeta = string.Equals(file, "skin.json", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "meta.json", StringComparison.OrdinalIgnoreCase);
                        if (ext != ".png" && !isMeta) continue;      // only images and skin metadata ever reach the disk
                        total += entry.Length;
                        if (total > MaxExtractedBytes) throw new InvalidDataException("The archive unpacks to more than " + MaxExtractedBytes / (1024 * 1024) + " MB.");

                        string dest = Path.GetFullPath(Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!dest.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (var src = entry.Open())
                        using (var dst = File.Create(dest))
                        {
                            // entry.Length comes from the archive header; enforce the cap on what is really written.
                            var buf = new byte[81920];
                            long written = 0;
                            int n;
                            while ((n = src.Read(buf, 0, buf.Length)) > 0)
                            {
                                written += n;
                                if (written > MaxExtractedBytes) throw new InvalidDataException("An archive entry is too large.");
                                dst.Write(buf, 0, n);
                            }
                        }
                        if (ext == ".png") pngs++;
                    }
                }
                if (pngs == 0) throw new InvalidDataException("The archive contains no PNG files.");

                var installed = new List<string>();
                foreach (var skinRoot in FindSkinRoots(tmp))
                {
                    bool isTop = string.Equals(Path.GetFullPath(skinRoot).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(tmp).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                    string name = CleanName(isTop ? Path.GetFileNameWithoutExtension(suggestedName ?? "") : Path.GetFileName(skinRoot));
                    if (name.Length == 0) name = "Downloaded Skin";

                    string target = Path.Combine(root, name);
                    if (Directory.Exists(target))
                    {
                        // Same link again = update in place; otherwise keep both.
                        bool same = State.I.Urls.TryGetValue(name, out var prevUrl) && prevUrl == sourceUrl;
                        if (same) SafeDelete(target);
                        else
                        {
                            int i = 2;
                            while (Directory.Exists(Path.Combine(root, name + " (" + i + ")"))) i++;
                            name = name + " (" + i + ")";
                            target = Path.Combine(root, name);
                        }
                    }
                    if (isTop)
                    {
                        Directory.CreateDirectory(target);
                        foreach (var d in Directory.GetDirectories(tmp)) Directory.Move(d, Path.Combine(target, Path.GetFileName(d)));
                        foreach (var f in Directory.GetFiles(tmp)) File.Move(f, Path.Combine(target, Path.GetFileName(f)));
                    }
                    else Directory.Move(skinRoot, target);

                    State.I.Urls[name] = sourceUrl;
                    installed.Add(name);
                    if (isTop) break;
                }
                State.Save();
                if (installed.Count == 0) throw new InvalidDataException("No skin folders were found in the archive.");
                return installed;
            }
            finally
            {
                SafeDelete(tmp);
            }
        }

        /// <summary>
        /// A skin root is a folder holding Body/, Head/, WoundView/ or Minigame/. Archives without
        /// that structure are treated as one skin made of whatever PNGs they contain.
        /// </summary>
        private static List<string> FindSkinRoots(string tmp)
        {
            var roots = new List<string>();
            var all = new List<string> { tmp };
            all.AddRange(Directory.GetDirectories(tmp, "*", SearchOption.AllDirectories));
            foreach (var dir in all.OrderBy(d => d.Length))
            {
                if (roots.Any(r => dir.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                bool hasCategory = Directory.GetDirectories(dir).Any(c => CategoryFolders.Contains(SkinLibrary.FolderKey(Path.GetFileName(c))));
                if (hasCategory) roots.Add(dir);
            }
            if (roots.Count > 0) return roots;

            // No category folders anywhere: the shallowest folder that directly contains PNGs.
            foreach (var dir in all.OrderBy(d => d.Length))
            {
                if (Directory.GetFiles(dir, "*.png").Length > 0) { roots.Add(dir); break; }
            }
            return roots;
        }

        private static string SafeRelative(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;
            string p = entryName.Replace('\\', '/');
            if (p.StartsWith("/") || p.Contains(":")) return null;
            foreach (var seg in p.Split('/'))
                if (seg == ".." || seg == ".") return null;
            return p;
        }

        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var bad = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray();
            string s = new string(chars).Trim().Trim('.', '_');
            if (s.Length > 48) s = s.Substring(0, 48).Trim();
            return s;
        }

        private static void SafeDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception e) { Plugin.Log.LogWarning("Could not remove " + dir + ": " + e.Message); }
        }
    }
}
