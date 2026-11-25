using System;
using System.Drawing;
using System.Windows.Forms;
using static GGSystemMonitor.Program;

namespace GGSystemMonitor
{
    internal sealed class WidgetListBox : ListBox
    {
        internal event Action<Point?> ContextRequested;

        protected override void WndProc(ref Message message)
        {
            const int ContextMenuMessage = 0x007B;
            if (message.Msg == ContextMenuMessage && ContextRequested != null)
            {
                int position = unchecked((int)message.LParam.ToInt64());
                // Shift+F10 and the Menu key target the selected row, not the mouse.
                ContextRequested(position == -1 ? (Point?)null :
                    PointToClient(new Point(unchecked((short)position), unchecked((short)(position >> 16)))));
                return;
            }
            base.WndProc(ref message);
        }
    }

    internal partial class SettingsForm
    {
        private LineItem _copiedWidget;

        private void WireWidgetListMenus(WidgetListBox list, Button add, Button up, Button down, Button remove)
        {
            var addMenu = new ContextMenuStrip { ShowImageMargin = false, AccessibleName = "Add widget" };
            foreach (var entry in DisplayNames)
            {
                var choice = new ToolStripMenuItem(entry.Value) { Name = "Add" + entry.Key };
                choice.Click += (s, e) => InsertWidget(list, list.Items.Count, NewWidget(entry.Key));
                addMenu.Items.Add(choice);
            }
            add.ContextMenuStrip = addMenu;
            add.Disposed += (s, e) => addMenu.Dispose();
            add.Click += (s, e) => addMenu.Show(add, new Point(add.Width, add.Height), ToolStripDropDownDirection.BelowLeft);
            var tip = new ToolTip();
            tip.SetToolTip(add, add.AccessibleName);
            add.Disposed += (s, e) => tip.Dispose();

            void UpdateButtons()
            {
                int index = list.SelectedIndex;
                up.Enabled = index > 0;
                down.Enabled = index >= 0 && index < list.Items.Count - 1;
                remove.Enabled = index >= 0;
            }
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            UpdateButtons();
            up.Click += (s, e) => MoveWidget(list, list.SelectedIndex, list.SelectedIndex - 1);
            down.Click += (s, e) => MoveWidget(list, list.SelectedIndex, list.SelectedIndex + 1);
            remove.Click += (s, e) => RemoveWidget(list, list.SelectedIndex);

            var actions = new ContextMenuStrip { ShowImageMargin = false, AccessibleName = "Widget actions" };
            list.ContextMenuStrip = actions;
            list.Disposed += (s, e) => actions.Dispose();
            LineItem target = null;
            int insertionIndex = 0;
            int IndexOfTarget() => target == null ? -1 : list.Items.IndexOf(target);
            ToolStripMenuItem ActionItem(string name, string label, Action invoke)
            {
                var action = new ToolStripMenuItem(label) { Name = name };
                action.Click += (s, e) => invoke();
                actions.Items.Add(action);
                return action;
            }
            var moveUp = ActionItem("MoveUp", "Move up", () => MoveWidget(list, IndexOfTarget(), IndexOfTarget() - 1));
            var moveDown = ActionItem("MoveDown", "Move down", () => MoveWidget(list, IndexOfTarget(), IndexOfTarget() + 1));
            var moveTop = ActionItem("MoveTop", "Move to top", () => MoveWidget(list, IndexOfTarget(), 0));
            var moveBottom = ActionItem("MoveBottom", "Move to bottom", () => MoveWidget(list, IndexOfTarget(), list.Items.Count - 1));
            actions.Items.Add(new ToolStripSeparator());
            var copy = ActionItem("Copy", "Copy", () => { if (target != null) _copiedWidget = target.Copy(); });
            var paste = ActionItem("Paste", "Paste", () =>
            {
                if (_copiedWidget != null) InsertWidget(list, Math.Min(insertionIndex, list.Items.Count), _copiedWidget.Copy());
            });
            paste.Enabled = false;
            actions.Items.Add(new ToolStripSeparator());
            var hide = ActionItem("Hide", "Hide", () =>
            {
                if (target == null) return;
                target.Hidden = !target.Hidden;
                list.Invalidate();
                EvaluateDirty();
            });
            var delete = ActionItem("Remove", "Remove", () => RemoveWidget(list, IndexOfTarget()));

            list.ContextRequested += position =>
            {
                if (position.HasValue && !list.ClientRectangle.Contains(position.Value)) return;
                int index = position.HasValue ? list.IndexFromPoint(position.Value) : list.SelectedIndex;
                if (index >= 0 && position.HasValue && !list.GetItemRectangle(index).Contains(position.Value)) index = -1;
                target = index >= 0 ? (LineItem)list.Items[index] : null;
                insertionIndex = index >= 0 ? index : list.Items.Count;
                if (target != null) list.SelectedIndex = index;
                moveUp.Enabled = moveTop.Enabled = index > 0;
                moveDown.Enabled = moveBottom.Enabled = index >= 0 && index < list.Items.Count - 1;
                copy.Enabled = hide.Enabled = delete.Enabled = target != null;
                hide.Text = target?.Hidden == true ? "Unhide" : "Hide";
                paste.Enabled = _copiedWidget != null;
                var anchor = position ?? (index < 0 ? Point.Empty : new Point(12, list.GetItemRectangle(index).Bottom));
                actions.Show(list, anchor);
            };
        }

        private static LineItem NewWidget(string type) => new LineItem
        {
            Type = type,
            Label = type == "Text" ? "Text" : GetFormatHint(type, false, false),
            DurationMs = type == "Weather" || type == "NowPlaying" ? 5000 : 0
        };

        private void InsertWidget(ListBox list, int index, LineItem item)
        {
            list.Items.Insert(index, item);
            list.SelectedIndex = index;
            EvaluateDirty();
        }

        private void MoveWidget(ListBox list, int index, int destination)
        {
            if (index < 0 || destination < 0 || destination >= list.Items.Count || index == destination) return;
            var item = list.Items[index];
            list.Items.RemoveAt(index);
            list.Items.Insert(destination, item);
            list.SelectedIndex = destination;
            EvaluateDirty();
        }

        private void RemoveWidget(ListBox list, int index)
        {
            if (index < 0 || index >= list.Items.Count) return;
            list.Items.RemoveAt(index);
            list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
            EvaluateDirty();
        }
    }
}
