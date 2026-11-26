using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace StillLyrics
{
    internal sealed class VersionPicker : Form
    {
        private readonly TextBox query = new TextBox();
        private readonly ListBox versions = new ListBox();
        private readonly Button select = new Button();
        private readonly Label status = new Label();
        private readonly Playback item;
        private readonly bool words, translation;
        private CancellationTokenSource search;
        public Edition Selection { get; private set; }

        internal VersionPicker(Playback item, bool words, bool translation)
        {
            this.item = item;
            this.words = words;
            this.translation = translation;
            Text = "选择歌词版本 · " + item.Title;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9);
            ClientSize = new Size(580, 315);
            MinimumSize = new Size(460, 300);
            TopMost = true;
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 5
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            foreach (int height in new[]
            {
                28,
                34,
                0,
                30,
                36
            }

            )
                panel.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            var label = new Label
            {
                Text = "查询曲名（可输入同一首歌的中文名或别名）",
                Dock = DockStyle.Fill
            };
            panel.Controls.Add(label, 0, 0);
            panel.SetColumnSpan(label, 2);
            query.Dock = DockStyle.Fill;
            query.Text = item.Title;
            query.AccessibleName = "查询曲名或别名";
            panel.Controls.Add(query, 0, 1);
            var find = new Button
            {
                Text = "查两个词库",
                Dock = DockStyle.Fill
            };
            panel.Controls.Add(find, 1, 1);
            versions.Dock = DockStyle.Fill;
            versions.HorizontalScrollbar = true;
            versions.AccessibleName = "候选歌词版本";
            panel.Controls.Add(versions, 0, 2);
            panel.SetColumnSpan(versions, 2);
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            panel.Controls.Add(status, 0, 3);
            panel.SetColumnSpan(status, 2);
            select.Text = "记住此版本";
            select.Dock = DockStyle.Fill;
            select.Enabled = false;
            panel.Controls.Add(select, 1, 4);
            var cancel = new Button
            {
                Text = "取消",
                Dock = DockStyle.Right,
                DialogResult = DialogResult.Cancel
            };
            panel.Controls.Add(cancel, 0, 4);
            Controls.Add(panel);
            CancelButton = cancel;
            AcceptButton = select;
            ActiveControl = query;
            find.Click += (s, e) => Search();
            query.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    Search();
                }
            };
            versions.SelectedIndexChanged += (s, e) => select.Enabled = versions.SelectedItem != null;
            versions.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    Accept();
                }
            };
            select.Click += (s, e) => Accept();
            versions.DoubleClick += (s, e) => Accept();
            Shown += (s, e) => Search();
            FormClosed += (s, e) =>
            {
                if (search != null)
                    search.Cancel();
            };
        }

        private sealed class Row
        {
            internal Edition Entry;
            public override string ToString()
            {
                var lyric = Entry.Decode();
                return string.Format("{0}  ·  {1} / {2}  ·  {3:m\\:ss}  ·  {4}{5}{6}", Entry.Source, Entry.Title, Entry.Artist, TimeSpan.FromSeconds(Entry.Duration), Entry.Album, lyric.HasWords ? "  ·  逐字" : "", lyric.HasTranslation ? "  ·  译文" : "");
            }
        }

        private async void Search()
        {
            if (search != null)
                search.Cancel();
            string title = query.Text.Trim();
            versions.Items.Clear();
            select.Enabled = false;
            if (title.Length == 0)
            {
                status.Text = "请输入要查找的曲名。";
                return;
            }

            var task = new CancellationTokenSource();
            search = task;
            status.Text = "正在查询网易云与 LRCLIB…";
            try
            {
                var result = await Catalog.Find(item, title, true, words, translation, task.Token);
                if (task.IsCancellationRequested || IsDisposed)
                    return;
                versions.Items.AddRange(result.Items.Select(x => (object)new Row { Entry = x }).ToArray());
                if (versions.Items.Count > 0)
                    versions.SelectedIndex = 0;
                status.Text = string.Format("播放器时长 {0:m\\:ss}   ·   {1} 个候选{2}", TimeSpan.FromSeconds(item.Duration), versions.Items.Count, result.Failures > 0 ? "   ·   部分词库暂不可用" : "");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !task.IsCancellationRequested)
                    status.Text = ex.Message;
            }
            finally
            {
                if (search == task)
                    search = null;
                task.Dispose();
            }
        }

        private void Accept()
        {
            var row = versions.SelectedItem as Row;
            if (row != null)
            {
                Selection = row.Entry;
                DialogResult = DialogResult.OK;
            }
        }
    }
}
