#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MusicHistory
{
    /// <summary>
    /// Where the MusicHistory pipeline repository (and its data folder: graphs, MIDI, SoundFont,
    /// recording previews) is. This viewer is its own repository and normally sits next to it:
    /// <code>
    ///   Desktop\MusicHistory\data\...     the pipeline and everything it produced
    ///   Desktop\MusicHistory-Viewer\      this Unity project
    /// </code>
    /// Resolution order: <c>-musicHistoryRoot &lt;dir&gt;</c> on the command line, the
    /// <c>MUSICHISTORY_ROOT</c> environment variable, the parent of <c>MUSICHISTORY_DATA</c>, a
    /// sibling folder named MusicHistory, and last the folder above this project (the layout
    /// before the viewer had its own repository, when it lived in MusicHistory/unity).
    /// A candidate counts when it holds a data folder or the pipeline's docs/DESIGN.md.
    /// </summary>
    public static class PipelinePaths
    {
        public const string RootFlag = "-musicHistoryRoot";
        static string? cached;

        /// <summary>The MusicHistory repository root.</summary>
        public static string Root()
        {
            if (cached != null) return cached;
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (string? candidate in Candidates(project))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string full = Path.GetFullPath(candidate);
                if (Directory.Exists(Path.Combine(full, "data")) || File.Exists(Path.Combine(full, "docs", "DESIGN.md")))
                    return cached = full;
            }
            // Nothing found: report the expected sibling location in any "missing" message.
            return cached = Path.GetFullPath(Path.Combine(project, "..", "MusicHistory"));
        }

        /// <summary><c>MUSICHISTORY_DATA</c> when set, else <see cref="Root"/>/data.</summary>
        public static string Data()
        {
            string? env = Environment.GetEnvironmentVariable("MUSICHISTORY_DATA");
            return !string.IsNullOrWhiteSpace(env) ? Path.GetFullPath(env) : Path.Combine(Root(), "data");
        }

        static IEnumerable<string?> Candidates(string project)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], RootFlag, StringComparison.OrdinalIgnoreCase))
                    yield return args[i + 1];
            yield return Environment.GetEnvironmentVariable("MUSICHISTORY_ROOT");
            string? data = Environment.GetEnvironmentVariable("MUSICHISTORY_DATA");
            if (!string.IsNullOrWhiteSpace(data)) yield return Path.GetDirectoryName(Path.GetFullPath(data));
            string parent = Path.GetDirectoryName(project) ?? project;
            yield return Path.Combine(parent, "MusicHistory");
            yield return parent;
        }
    }
}
