using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HddBackup.Core.Browsing;
using HddBackup.Core.Editing;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.App.Localization;

namespace HddBackup.App.Forms
{
    /// <summary>Edit mode of the Files tab (feature 010): changes wait in a diff file until Save.</summary>
    internal sealed partial class FilesTab
    {
        private readonly Button _edit = Ui.Button();
        private readonly Button _save = Ui.Button();
        private readonly Button _discard = Ui.Button();
        private readonly Button _apply = Ui.Button();
        private readonly ToolStripMenuItem _menuNewFile = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _menuNewFolder = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _menuRename = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _menuDelete = new ToolStripMenuItem();
        private readonly ToolStripSeparator _menuEditSeparator = new ToolStripSeparator();
        private ImageEditSession _session;
        private PendingTree _pending;
        private CardEditSession _card;

        private IFileTree Tree => (IFileTree)_pending ?? _view;

        private bool Editing => _session != null || _card != null;

        private void InitEdit()
        {
            _menu.Items.Add(_menuEditSeparator);
            _menu.Items.Add(_menuNewFile);
            _menu.Items.Add(_menuNewFolder);
            _menu.Items.Add(_menuRename);
            _menu.Items.Add(_menuDelete);
            _menu.Opening += (s, e) => UpdateEditUi();
            _edit.Click += (s, e) => BeginEdit(false);
            _save.Click += (s, e) => SaveEdits();
            _discard.Click += (s, e) => DiscardEdits();
            _apply.Click += (s, e) => ApplyText();
            _menuNewFile.Click += (s, e) => CreateEntry(false);
            _menuNewFolder.Click += (s, e) => CreateEntry(true);
            _menuRename.Click += (s, e) => RenameSelected();
            _menuDelete.Click += (s, e) => DeleteSelected();
            _tree.KeyDown += (s, e) =>
            {
                if (!Editing)
                {
                    return;
                }

                if (e.KeyCode == Keys.F2)
                {
                    RenameSelected();
                }
                else if (e.KeyCode == Keys.Delete)
                {
                    DeleteSelected();
                }
            };
            _viewer.TextChanged += (s, e) => UpdateEditUi();
        }

        private void ApplyEditStrings()
        {
            _edit.Text = Strings.Get("Edit_Button");
            _save.Text = Strings.Get("Edit_Save");
            _discard.Text = Strings.Get("Edit_Discard");
            _apply.Text = Strings.Get("Edit_Apply");
            _tips.SetToolTip(_edit, Strings.Get("Tooltip_Edit_Button"));
            _tips.SetToolTip(_save, Strings.Get("Tooltip_Edit_Save"));
            _tips.SetToolTip(_discard, Strings.Get("Tooltip_Edit_Discard"));
            _menuNewFile.Text = Strings.Get("Edit_NewFile");
            _menuNewFolder.Text = Strings.Get("Edit_NewFolder");
            _menuRename.Text = Strings.Get("Edit_Rename");
            _menuDelete.Text = Strings.Get("Edit_Delete");
            UpdateEditUi();
        }

        /// <summary>Shows or hides the edit controls for the current state.</summary>
        private void UpdateEditUi()
        {
            bool open = _view != null;
            _edit.Visible = !Editing;
            _edit.Enabled = open && !_planning && !_toolRunning;
            _save.Visible = _session != null;
            _discard.Visible = Editing;
            _save.Enabled = _session != null && _session.Changes.Count > 0;
            _discard.Enabled = _card != null || (_session != null && _session.Changes.Count > 0);
            _discard.Text = Strings.Get(_card != null ? "Edit_Done" : "Edit_Discard");
            _tips.SetToolTip(_discard, Strings.Get(_card != null ? "Tooltip_Edit_Done" : "Tooltip_Edit_Discard"));
            var entry = _tree.SelectedNode?.Tag as FsEntry;
            bool selected = Editing && entry != null;
            bool isRoot = entry != null && entry.Path == "/";
            _menuEditSeparator.Visible = Editing;
            _menuNewFile.Visible = _menuNewFolder.Visible = _menuRename.Visible = _menuDelete.Visible = Editing;
            _menuNewFile.Enabled = _menuNewFolder.Enabled = selected;
            _menuRename.Enabled = _menuDelete.Enabled = selected && !isRoot;
            _menuExtract.Enabled = _menuExtract.Enabled && !Editing;
            _extract.Enabled = _extract.Enabled && !Editing;
            _apply.Visible = Editing && !_viewer.ReadOnly;
            _apply.Enabled = _apply.Visible && _viewerEditedFrom != null && _viewer.Text != _viewerEditedFrom;
            if (Editing)
            {
                SetStatus(() => _card != null ? Strings.Get("Edit_EditingCard") : Strings.Format("Edit_Editing", _session.Changes.Count), false);
            }
        }

        private string _viewerEditedFrom;

        /// <summary>Called at the end of RenderViewer: makes the text box editable for a fully shown text file in edit mode.</summary>
        private void UpdateViewerEditable()
        {
            var entry = _shownEntry;
            var preview = _shownPreview;
            bool can = Editing && entry != null && entry.Kind == FsEntryKind.File && entry.HasContent && preview != null
                && preview.Mode == PreviewMode.Text && !preview.Truncated && _viewerError == null && !_viewHex.Checked;
            _viewer.ReadOnly = !can;
            _viewerEditedFrom = can ? _viewer.Text : null;
            UpdateEditUi();
        }

        private void BeginEdit(bool automatic)
        {
            if (_view == null || _source == null)
            {
                return;
            }

            if (DeviceMode)
            {
                BeginCardEdit();
                return;
            }

            if (_openedPath == null)
            {
                return;
            }

            var item = _partition.SelectedItem as PartitionItem;
            ImageEditSession session;
            try
            {
                session = ImageEditSession.Open(_openedPath, item?.Partition.Number ?? 0);
            }
            catch (EditRefusedException ex)
            {
                if (!automatic)
                {
                    MessageBox.Show(this, Strings.Get("Edit_Why_" + ex.Reason), Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ImagingException)
            {
                MessageBox.Show(this, ex.Message, Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (session.State == SessionState.Interrupted)
            {
                if (MessageBox.Show(this, Strings.Get("Edit_Interrupted"), Strings.Get("Edit_FinishSave"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                {
                    RunSave(session, true);
                }
                else
                {
                    session.Dispose();
                }

                return;
            }

            if (session.IsStale)
            {
                if (MessageBox.Show(this, Strings.Get("Edit_ImageChanged"), Strings.Get("Edit_Discard"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    session.Discard();
                    session.Dispose();
                    BeginEdit(false);
                }
                else
                {
                    session.Dispose();
                }

                return;
            }

            _session = session;
            RebuildTree(_tree.SelectedNode?.Tag is FsEntry current ? current.Path : "/");
        }

        private void BeginCardEdit()
        {
            var item = _partition.SelectedItem as PartitionItem;
            var device = _openedDevice;
            if (item == null || device == null)
            {
                return;
            }

            CardEditSession card;
            try
            {
                card = CardEditSession.Open(_access, device, item.Partition);
            }
            catch (EditRefusedException ex)
            {
                MessageBox.Show(this, Strings.Get("Edit_Why_" + ex.Reason), Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ImagingException)
            {
                MessageBox.Show(this, ex.Message, Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(this, Strings.Get("Edit_CardConfirm"), Strings.Get("Edit_Button"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                card.Dispose();
                return;
            }

            _card = card;
            RebuildTree(_tree.SelectedNode?.Tag is FsEntry current ? current.Path : "/");
        }

        private void EndEdit()
        {
            var card = _card;
            _card = null;
            card?.Dispose();
            var session = _session;
            _session = null;
            _pending = null;
            session?.Dispose();
            _viewer.ReadOnly = true;
            UpdateEditUi();
        }

        /// <summary>Rebuilds the tree from the real tree plus the waiting changes and selects <paramref name="select"/> if it still exists.</summary>
        private void RebuildTree(string select)
        {
            _previewCts?.Cancel();
            if (_card != null)
            {
                // The card changed under the open view; read the file system again.
                var old = _view;
                _view = _source.OpenFileSystem(((PartitionItem)_partition.SelectedItem).Partition);
                old.Dispose();
                _pending = new PendingTree(_view, new EditChange[0]);
            }
            else
            {
                _pending = new PendingTree(_view, _session.Changes);
            }

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                var root = CreateNode(_pending.Root);
                root.Text = "/";
                _tree.Nodes.Add(root);
                var node = root;
                LoadSync(node);
                node.Expand();
                foreach (string part in select.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is FsEntry f && f.Name == part);
                    if (next == null)
                    {
                        break;
                    }

                    node = next;
                    if (((FsEntry)node.Tag).IsDirectory)
                    {
                        LoadSync(node);
                        node.Expand();
                    }
                }

                _tree.SelectedNode = node;
            }
            finally
            {
                _tree.EndUpdate();
            }

            ShowEntry(_tree.SelectedNode);
            UpdateEditUi();
        }

        private void LoadSync(TreeNode node)
        {
            if (node.Nodes.Count == 1 && ReferenceEquals(node.Nodes[0].Tag, LoadingTag))
            {
                var children = _pending.GetChildren((FsEntry)node.Tag, CancellationToken.None);
                node.Nodes.Clear();
                foreach (var child in children)
                {
                    node.Nodes.Add(CreateNode(child));
                }
            }
        }

        private bool Record(EditChange change, string select)
        {
            if (_card != null)
            {
                return RecordOnCard(change, select);
            }

            try
            {
                _session.Add(change);
            }
            catch (EditException ex)
            {
                MessageBox.Show(this, Strings.Format("Edit_ChangeRefused", EditTexts.Error(ex.Code)), Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, Strings.Format("Edit_ChangeRefused", ex.Message), Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            RebuildTree(select);
            return true;
        }

        private bool RecordOnCard(EditChange change, string select)
        {
            SaveResult result;
            var previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                result = _card.Apply(change, null, CancellationToken.None);
            }
            finally
            {
                Cursor = previous;
            }

            if (result.Status != SaveStatus.Saved)
            {
                string why = result.Error != null ? EditTexts.Error(result.Error.Value) : result.Detail;
                MessageBox.Show(
                    this,
                    result.Status == SaveStatus.Refused ? Strings.Format("Edit_ChangeRefused", why) : Strings.Format("Edit_CardFailed", why),
                    Strings.Get("Edit_Button"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                if (result.Status == SaveStatus.Refused)
                {
                    return false;
                }
            }

            RebuildTree(select);
            return result.Status == SaveStatus.Saved;
        }

        private void ApplyText()
        {
            var entry = _shownEntry;
            if (!Editing || entry == null || _viewerEditedFrom == null)
            {
                return;
            }

            byte[] original;
            try
            {
                original = _pending.ReadAll(entry, FilePreview.TextLimit);
            }
            catch (Exception ex) when (ex is ImagingException || ex is IOException || ex is InvalidOperationException)
            {
                MessageBox.Show(this, ex.Message, Strings.Get("Edit_Button"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            bool cr = Array.IndexOf(original, (byte)13) >= 0;
            string text = _viewer.Text;
            if (!cr)
            {
                text = text.Replace("\r\n", "\n");
            }

            var body = new UTF8Encoding(false).GetBytes(text);
            var data = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
            Record(EditChange.Edit(entry.Path, data, _pending.OriginalStampFor(entry.Path)), entry.Path);
        }

        private void CreateEntry(bool directory)
        {
            var entry = _tree.SelectedNode?.Tag as FsEntry;
            if (!Editing || entry == null)
            {
                return;
            }

            string parent = entry.IsDirectory ? entry.Path : EditPath.ParentOf(entry.Path);
            string name = AskName(Strings.Get(directory ? "Edit_NewFolder" : "Edit_NewFile"), string.Empty, parent);
            if (name == null)
            {
                return;
            }

            string path = EditChange.Join(parent, name);
            Record(directory ? EditChange.NewDirectory(path) : EditChange.NewFile(path, new byte[0]), path);
        }

        private void RenameSelected()
        {
            var entry = _tree.SelectedNode?.Tag as FsEntry;
            if (!Editing || entry == null || entry.Path == "/")
            {
                return;
            }

            string parent = EditPath.ParentOf(entry.Path);
            string name = AskName(Strings.Get("Edit_Rename"), entry.Name, parent);
            if (name == null || name == entry.Name)
            {
                return;
            }

            Record(EditChange.Move(entry.Path, name, _pending.OriginalStampFor(entry.Path)), EditChange.Join(parent, name));
        }

        private void DeleteSelected()
        {
            var entry = _tree.SelectedNode?.Tag as FsEntry;
            if (!Editing || entry == null || entry.Path == "/")
            {
                return;
            }

            if (MessageBox.Show(this, Strings.Format("Edit_DeleteQuestion", entry.Path), Strings.Get("Edit_Delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            Record(EditChange.Remove(entry.Path, _pending.OriginalStampFor(entry.Path)), EditPath.ParentOf(entry.Path));
        }

        /// <summary>Asks for a name until it is valid and free in <paramref name="parent"/>; null when cancelled.</summary>
        private string AskName(string title, string initial, string parent)
        {
            using (var dialog = new NameDialog(title, initial, name =>
            {
                var bad = EditNames.Validate(name);
                if (bad != null)
                {
                    return EditTexts.Error(bad.Value);
                }

                return _pending.Exists(EditChange.Join(parent, name)) ? EditTexts.Error(EditError.NameExists) : null;
            }))
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.EnteredName : null;
            }
        }

        private void DiscardEdits()
        {
            if (!Editing)
            {
                return;
            }

            if (_session != null && _session.Changes.Count > 0
                && MessageBox.Show(this, Strings.Get("Edit_DiscardQuestion"), Strings.Get("Edit_Discard"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            _session?.Discard();
            LeaveEditMode();
        }

        private void LeaveEditMode()
        {
            string select = _tree.SelectedNode?.Tag is FsEntry e ? e.Path : "/";
            EndEdit();
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            var root = CreateNode(_view.Root);
            root.Text = "/";
            _tree.Nodes.Add(root);
            _tree.SelectedNode = root;
            root.Expand();
            _tree.EndUpdate();
            SetStatus(() => string.Empty, false);
            UpdateExtractEnabled();
        }

        private void SaveEdits()
        {
            if (_session == null || _session.Changes.Count == 0)
            {
                return;
            }

            if (_apply.Enabled)
            {
                ApplyText();
            }

            var session = _session;
            _session = null;
            _pending = null;
            RunSave(session, false);
        }

        /// <summary>Closes the image for reading, writes the changes with a progress window, reopens the image.</summary>
        private void RunSave(ImageEditSession session, bool finish)
        {
            string path = _openedPath;
            CloseSource();
            SaveResult result;
            using (var dialog = new SaveChangesDialog(session, finish))
            {
                dialog.ShowDialog(this);
                result = dialog.Result;
            }

            session.Dispose();
            OpenSelectedImage();
            string text;
            bool error = false;
            switch (result?.Status)
            {
                case SaveStatus.Saved:
                    text = Strings.Format("Edit_Saved", result.ChangesWritten);
                    break;
                case SaveStatus.SavedChecksumStale:
                    text = Strings.Format("Edit_SavedStale", result.Detail);
                    error = true;
                    break;
                case SaveStatus.Interrupted:
                    text = Strings.Get("Edit_Interrupted");
                    error = true;
                    break;
                default:
                    text = Strings.Format("Edit_SaveFailed", result == null ? string.Empty : result.Error != null ? EditTexts.Error(result.Error.Value) : result.Detail);
                    error = true;
                    break;
            }

            // OpenSelectedImage clears the status line; show the result once it has started.
            BeginInvoke((Action)(() => SetStatus(() => text, error)));
        }

        /// <summary>Re-enters edit mode after opening an image that still has waiting changes.</summary>
        private void RestoreEditIfWaiting()
        {
            if (_openedPath != null && !DeviceMode && (File.Exists(DiffFile.PathFor(_openedPath)) || File.Exists(RedoLog.PathFor(_openedPath))))
            {
                BeginEdit(true);
            }
        }
    }

    internal static class EditPath
    {
        public static string ParentOf(string path)
        {
            EditChange.Split(path, out string parent, out string _);
            return parent;
        }
    }

    internal static class EditTexts
    {
        public static string Error(EditError code)
        {
            switch (code)
            {
                case EditError.NoSpace: return Strings.Get("Edit_Err_NoSpace");
                case EditError.NoInodes: return Strings.Get("Edit_Err_NoInodes");
                case EditError.TooFragmented: return Strings.Get("Edit_Err_TooFragmented");
                case EditError.NotFound: return Strings.Get("Edit_Err_NotFound");
                case EditError.EntryChanged: return Strings.Get("Edit_Err_EntryChanged");
                case EditError.NameExists: return Strings.Get("Edit_Err_NameExists");
                case EditError.BadName: return Strings.Get("Edit_Err_BadName");
                case EditError.TooLarge: return Strings.Get("Edit_Err_TooLarge");
                default: return Strings.Get("Edit_Err_Other");
            }
        }
    }

    /// <summary>Asks for one name; the validator gives the reason a name is not acceptable (or null).</summary>
    internal sealed class NameDialog : Form
    {
        private readonly TextBox _box = new TextBox { Dock = DockStyle.Top };
        private readonly Label _error = new Label { AutoSize = true, ForeColor = Color.DarkRed, Dock = DockStyle.Top };
        private readonly Button _ok = Ui.Button();
        private readonly Button _cancel = Ui.Button();
        private readonly Func<string, string> _validate;

        public NameDialog(string title, string initial, Func<string, string> validate)
        {
            _validate = validate;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            ClientSize = new Size(380, 120);
            var caption = new Label { Text = Strings.Get("Edit_NamePrompt"), AutoSize = true, Dock = DockStyle.Top };
            _ok.Text = "OK";
            _cancel.Text = Strings.Get("Edit_Cancel");
            _ok.DialogResult = DialogResult.OK;
            _cancel.DialogResult = DialogResult.Cancel;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_ok);
            Controls.Add(_error);
            Controls.Add(_box);
            Controls.Add(caption);
            Controls.Add(buttons);
            AcceptButton = _ok;
            CancelButton = _cancel;
            _box.Text = initial;
            _box.TextChanged += (s, e) => Check();
            Check();
        }

        public string EnteredName => _box.Text;

        private void Check()
        {
            string reason = _box.Text.Length == 0 ? string.Empty : _validate(_box.Text);
            _error.Text = reason ?? string.Empty;
            _ok.Enabled = _box.Text.Length > 0 && reason == null;
        }
    }

    /// <summary>Writes the waiting changes into the image and shows the progress; Cancel before the commit point leaves the image untouched.</summary>
    internal sealed class SaveChangesDialog : Form
    {
        private readonly ImageEditSession _session;
        private readonly bool _finish;
        private readonly Label _label = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
        private readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Minimum = 0, Maximum = 1000 };
        private readonly Button _cancel = Ui.Button();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public SaveChangesDialog(ImageEditSession session, bool finish)
        {
            _session = session;
            _finish = finish;
            Text = Strings.Get("Edit_Save");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            ControlBox = false;
            ClientSize = new Size(420, 110);
            Padding = new Padding(12);
            _label.Text = Strings.Get("Edit_Saving");
            _cancel.Text = Strings.Get("Edit_Cancel");
            _cancel.Dock = DockStyle.Bottom;
            _cancel.Click += (s, e) => _cts.Cancel();
            Controls.Add(_bar);
            Controls.Add(_label);
            Controls.Add(_cancel);
        }

        public SaveResult Result { get; private set; }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Action<SavePhase, long, long> progress = (phase, done, total) =>
            {
                if (IsDisposed)
                {
                    return;
                }

                BeginInvoke((Action)(() =>
                {
                    _label.Text = Strings.Get(phase == SavePhase.Checksum ? "Edit_UpdatingChecksum" : "Edit_Saving");
                    _bar.Value = total <= 0 ? 0 : (int)Math.Min(1000, done * 1000 / total);
                }));
            };
            var ct = _cts.Token;
            Result = await Task.Run(() => _finish ? _session.FinishSave(progress, ct) : _session.Save(progress, ct));
            Close();
        }
    }
}
