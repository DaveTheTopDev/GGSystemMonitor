using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using static GGSystemMonitor.Program;

namespace GGSystemMonitor
{
    internal sealed class PriorityOrder
    {
        private readonly List<List<LineItem>> levels;
        private readonly Dictionary<LineItem, int> lineOrder;
        internal IReadOnlyList<List<LineItem>> Levels => levels;

        internal PriorityOrder(IEnumerable<LineItem> items)
        {
            var widgets = items.ToArray();
            lineOrder = widgets.Select((item, index) => (item, index)).ToDictionary(entry => entry.item, entry => entry.index);
            levels = widgets.Where(item => item.PriorityMode != 0)
                .GroupBy(item => Math.Max(1, item.PriorityRank))
                .OrderByDescending(group => group.Key).Select(group => group.ToList()).ToList();
        }

        private int Index(LineItem item) => levels.FindIndex(group => group.Contains(item));
        internal int Level(LineItem item) => Index(item) + 1;

        internal bool CanMove(LineItem item, int direction)
        {
            int index = Index(item);
            return (direction == -1 || direction == 1) && index >= 0 &&
                (levels[index].Count > 1 || (index + direction >= 0 && index + direction < levels.Count));
        }

        internal bool Move(LineItem item, int direction)
        {
            if (!CanMove(item, direction)) return false;
            int index = Index(item);
            var current = levels[index];
            if (current.Count > 1)
            {
                current.Remove(item);
                levels.Insert(direction < 0 ? index : index + 1, new List<LineItem> { item });
            }
            else
            {
                var joined = levels[index + direction];
                joined.Add(item);
                joined.Sort((a, b) => lineOrder[a].CompareTo(lineOrder[b]));
                levels.RemoveAt(index);
            }
            return true;
        }

        internal void Apply()
        {
            for (int i = 0; i < levels.Count; i++)
                foreach (var item in levels[i]) item.PriorityRank = levels.Count - i;
        }
    }

    internal sealed class PriorityOrderDialog : Form
    {
        private readonly PriorityOrder order;
        private readonly TreeView tree;
        private readonly Button moveUp, moveDown;
        private readonly Label status;
        private LineItem Current => tree.SelectedNode?.Tag as LineItem;

        internal PriorityOrderDialog(PriorityOrder order, LineItem selected, bool topLine)
        {
            this.order = order;
            Text = topLine ? "Top line priority" : "Bottom line priority";
            Name = "PriorityOrderDialog";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(475, 432);
            MinimumSize = new Size(420, 380);
            MaximizeBox = MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(245, 245, 248);
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            var intro = new Label
            {
                Text = "Highest priority is at the top. Widgets in the same level take turns.",
                Location = new Point(14, 14), Size = new Size(448, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            tree = new TreeView
            {
                Name = "PriorityLevels", AccessibleName = "Priority levels, highest first",
                AccessibleDescription = "Select a widget, then use Up or Down to move it between priority levels.",
                Location = new Point(14, 44), Size = new Size(447, 260), FullRowSelect = true,
                HideSelection = false, ShowLines = false, ShowPlusMinus = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            status = new Label
            {
                Location = new Point(14, 310), Size = new Size(447, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = Color.FromArgb(70, 79, 99)
            };
            moveUp = MakeButton("Move up", 14, 350);
            moveDown = MakeButton("Move down", 114, 350);
            var cancel = MakeButton("Cancel", 272, 390, true);
            cancel.DialogResult = DialogResult.Cancel;
            var save = MakeButton("Apply", 372, 390, true);
            save.FlatStyle = FlatStyle.Flat;
            save.BackColor = Color.FromArgb(0, 100, 185);
            save.ForeColor = Color.White;
            save.FlatAppearance.BorderSize = 0;
            save.DialogResult = DialogResult.OK;
            AcceptButton = save;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { intro, tree, status, moveUp, moveDown, cancel, save });
            tree.BeforeCollapse += (s, e) => e.Cancel = true;
            tree.AfterSelect += (s, e) => UpdateActions();
            tree.KeyDown += (s, e) =>
            {
                if (e.Modifiers != Keys.None || (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down)) return;
                e.Handled = e.SuppressKeyPress = true;
                Shift(e.KeyCode == Keys.Up ? -1 : 1);
            };
            moveUp.Click += (s, e) => Shift(-1);
            moveDown.Click += (s, e) => Shift(1);
            Rebuild(selected);
            Shown += (s, e) => tree.Focus();
        }

        private static Button MakeButton(string text, int x, int y, bool right = false) =>
            new Button { Text = text, Location = new Point(x, y), Size = new Size(89, 28),
                Anchor = AnchorStyles.Bottom | (right ? AnchorStyles.Right : AnchorStyles.Left) };

        private void Shift(int direction)
        {
            var item = Current;
            if (item != null && order.Move(item, direction)) Rebuild(item);
        }

        private void Rebuild(LineItem selected)
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode target = null;
            for (int i = 0; i < order.Levels.Count; i++)
            {
                var level = order.Levels[i];
                var group = tree.Nodes.Add("Priority " + (i + 1) + (i == 0 ? " (highest)" : "") +
                    (level.Count > 1 ? "  ·  " + level.Count + " tied" : ""));
                foreach (var item in level)
                {
                    var node = group.Nodes.Add(item.ListDisplayName);
                    node.Tag = item;
                    if (ReferenceEquals(item, selected)) target = node;
                }
                group.Expand();
            }
            tree.SelectedNode = target;
            target?.EnsureVisible();
            tree.EndUpdate();
            UpdateActions();
        }

        private void UpdateActions()
        {
            var current = Current;
            moveUp.Enabled = current != null && order.CanMove(current, -1);
            moveDown.Enabled = current != null && order.CanMove(current, 1);
            if (current == null) status.Text = "Select a widget to change its priority.";
            else if (!moveUp.Enabled && !moveDown.Enabled) status.Text = "Enable takeover on another widget to set a relative priority.";
            else status.Text = "Use Up and Down to join a neighboring level or split a tied widget into its own level.";
        }
    }
}
