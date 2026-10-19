using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpotiBee.UI
{
    /// <summary>A text box with grey hint text, where Esc clears the text instead of closing the window.</summary>
    internal sealed class FilterBox : TextBox
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public string Hint { get; set; } = "Filter";

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // wParam 1: keep showing the hint while the box has focus
            SendMessage(Handle, EM_SETCUEBANNER, (IntPtr)1, Hint);
        }

        // Claiming Esc while there's text stops the form's Cancel button from seeing it
        protected override bool IsInputKey(Keys keyData) =>
            (keyData == Keys.Escape && TextLength > 0) || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && TextLength > 0)
            {
                Clear();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }

    /// <summary>
    /// Shows only the rows of a checkbox list whose name (first column) contains every word typed in
    /// the filter box. Ticks on hidden rows are kept, so they still count. A ListView can't hide
    /// rows, so the matching ones are put back into it each time the filter changes.
    /// </summary>
    internal sealed class ListFilter
    {
        private readonly ListView list;
        private readonly TextBox box;
        private readonly HashSet<ListViewItem> ticked = new HashSet<ListViewItem>();
        private List<ListViewItem> all = new List<ListViewItem>();

        public ListFilter(ListView list, TextBox box)
        {
            this.list = list;
            this.box = box;
            box.TextChanged += (s, e) => Apply();
            list.ItemChecked += (s, e) =>
            {
                if (Changing)
                    return;
                if (e.Item.Checked)
                    ticked.Add(e.Item);
                else
                    ticked.Remove(e.Item);
                CheckedChanged?.Invoke();
            };
        }

        /// <summary>
        /// True while the filter itself is ticking or showing rows. A list's ItemCheck handler that
        /// blocks the user's clicks should let these through.
        /// </summary>
        public bool Changing { get; private set; }

        /// <summary>Raised after rows are ticked or unticked, or the filter changes what's shown.</summary>
        public event Action CheckedChanged;

        public IReadOnlyList<ListViewItem> AllItems => all;

        /// <summary>Ticked rows in list order, hidden ones included.</summary>
        public List<ListViewItem> CheckedItems => all.Where(ticked.Contains).ToList();

        public int HiddenCheckedCount => ticked.Count(i => i.ListView == null);

        /// <summary>Replaces the rows. Rows already ticked stay ticked.</summary>
        public void SetItems(IEnumerable<ListViewItem> items)
        {
            all = items.ToList();
            ticked.Clear();
            foreach (var item in all.Where(i => i.Checked))
                ticked.Add(item);
            Apply();
        }

        /// <summary>Ticks or unticks a row, shown or not, bypassing the list's ItemCheck rules.</summary>
        public void SetChecked(ListViewItem item, bool value)
        {
            Changing = true;
            try { item.Checked = value; }
            finally { Changing = false; }
            if (value)
                ticked.Add(item);
            else
                ticked.Remove(item);
            CheckedChanged?.Invoke();
        }

        private void Apply()
        {
            var words = box.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var shown = all.Where(i => words.All(w => i.Text.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToArray();
            Changing = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var item in shown)
                    item.Checked = ticked.Contains(item);
                list.Items.AddRange(shown);
            }
            finally
            {
                list.EndUpdate();
                Changing = false;
            }
            CheckedChanged?.Invoke();
        }
    }
}
