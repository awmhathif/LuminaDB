using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace LuminaDB.App.ViewModels
{
    internal static class ListSync
    {
        /// <summary>
        /// Makes <paramref name="list"/> hold <paramref name="items"/>, in order, by changing it in place: nothing happens
        /// when it already does, and an item that stays is never removed. A drop-down showing that list therefore keeps
        /// its choice (emptying and refilling it made the drop-down drop its choice, and pages are kept between visits).
        /// </summary>
        public static void Sync(this ObservableCollection<string> list, IList<string> items)
        {
            if (list == null || items == null) return;
            var wanted = new HashSet<string>(items, StringComparer.Ordinal);
            for (int i = list.Count - 1; i >= 0; i--)
                if (!wanted.Contains(list[i])) list.RemoveAt(i);
            for (int i = 0; i < items.Count; i++)
            {
                if (i < list.Count && string.Equals(list[i], items[i], StringComparison.Ordinal)) continue;
                int at = -1;
                for (int j = i + 1; j < list.Count; j++)
                    if (string.Equals(list[j], items[i], StringComparison.Ordinal)) { at = j; break; }
                if (at >= 0) list.Move(at, i);
                else list.Insert(i, items[i]);
            }
            while (list.Count > items.Count) list.RemoveAt(list.Count - 1);
        }
    }
}
