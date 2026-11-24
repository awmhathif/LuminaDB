using System;
using System.IO;
using System.Runtime.Serialization;
using LuminaDB.Core;

namespace LuminaDB.App.ViewModels
{
    /// <summary>How the user left the layout: kept in config\ui.json so the app opens the way it was.</summary>
    [DataContract]
    public sealed class UiState
    {
        [DataMember] public bool NotebookFocus { get; set; }
        /// <summary>"split", "editor" or "results".</summary>
        [DataMember] public string NotebookLayout { get; set; } = "split";
        /// <summary>The editor's share of the editor + results height in split view (0.2–0.85).</summary>
        [DataMember] public double NotebookSplit { get; set; } = 0.55;
        /// <summary>Focus mode keeps these open when pinned.</summary>
        [DataMember] public bool NotebookIndexPinned { get; set; }
        [DataMember] public bool NotebookReceiptPinned { get; set; }

        public static string PathFor(AppPaths p) => Path.Combine(p.Config, "ui.json");

        public static UiState Load(AppPaths p)
        {
            var s = JsonFile.Read<UiState>(PathFor(p)) ?? new UiState();
            if (s.NotebookLayout != "editor" && s.NotebookLayout != "results") s.NotebookLayout = "split";
            if (double.IsNaN(s.NotebookSplit) || s.NotebookSplit < 0.2 || s.NotebookSplit > 0.85) s.NotebookSplit = 0.55;
            return s;
        }

        public void Save(AppPaths p)
        {
            try { JsonFile.Write(PathFor(p), this); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("ui.json save failed", ex); }
        }
    }
}
