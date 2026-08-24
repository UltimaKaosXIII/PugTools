namespace PugTools {
  partial class WorldBrowser {
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing) {
      if (disposing && (components != null)) {
        components.Dispose();
      }
      base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent() {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(WorldBrowser));
            this.splitContainer1 = new System.Windows.Forms.SplitContainer();
            this.splitContainer2 = new System.Windows.Forms.SplitContainer();
            this.treeViewFast1 = new TreeViewFast.Controls.TreeViewFast();
            this.imageList1 = new System.Windows.Forms.ImageList(this.components);
            this.btnWorldBrowserHelp = new System.Windows.Forms.Button();
            this.btnStopRender = new System.Windows.Forms.Button();
            this.btnDataCollapse = new System.Windows.Forms.Button();
            this.btnToggleDBO = new System.Windows.Forms.Button();
            this.splitContainer3 = new System.Windows.Forms.SplitContainer();
            this.renderPanel = new System.Windows.Forms.Panel();
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.splitContainer4 = new System.Windows.Forms.SplitContainer();
            this.tvfDataViewer = new TreeViewFast.Controls.TreeViewFast();
            this.dgvDataViewer = new System.Windows.Forms.DataGridView();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripProgressBar1 = new System.Windows.Forms.ToolStripProgressBar();
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.toolStripMenuItemViewAll = new System.Windows.Forms.ToolStripMenuItem();
            this.contextMenuStrip2 = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.toolStripMenuItem1 = new System.Windows.Forms.ToolStripMenuItem();
            this.BodyTypeStrip = new System.Windows.Forms.ContextMenuStrip(this.components);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).BeginInit();
            this.splitContainer1.Panel1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).BeginInit();
            this.splitContainer2.Panel1.SuspendLayout();
            this.splitContainer2.Panel2.SuspendLayout();
            this.splitContainer2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).BeginInit();
            this.splitContainer3.Panel1.SuspendLayout();
            this.splitContainer3.Panel2.SuspendLayout();
            this.splitContainer3.SuspendLayout();
            this.renderPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer4)).BeginInit();
            this.splitContainer4.Panel1.SuspendLayout();
            this.splitContainer4.Panel2.SuspendLayout();
            this.splitContainer4.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvDataViewer)).BeginInit();
            this.statusStrip1.SuspendLayout();
            this.contextMenuStrip1.SuspendLayout();
            this.contextMenuStrip2.SuspendLayout();
            this.SuspendLayout();
            // 
            // splitContainer1
            // 
            this.splitContainer1.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
            this.splitContainer1.Cursor = System.Windows.Forms.Cursors.Default;
            this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer1.Location = new System.Drawing.Point(0, 0);
            this.splitContainer1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.splitContainer1.Name = "splitContainer1";
            // 
            // splitContainer1.Panel1
            // 
            this.splitContainer1.Panel1.Controls.Add(this.splitContainer2);
            // 
            // splitContainer1.Panel2
            // 
            this.splitContainer1.Panel2.Controls.Add(this.splitContainer3);
            this.splitContainer1.Panel2.Controls.Add(this.statusStrip1);
            this.splitContainer1.Size = new System.Drawing.Size(1904, 1041);
            this.splitContainer1.SplitterDistance = 350;
            this.splitContainer1.SplitterWidth = 5;
            this.splitContainer1.TabIndex = 0;
            // 
            // splitContainer2
            // 
            this.splitContainer2.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
            this.splitContainer2.Cursor = System.Windows.Forms.Cursors.Arrow;
            this.splitContainer2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer2.FixedPanel = System.Windows.Forms.FixedPanel.Panel2;
            this.splitContainer2.IsSplitterFixed = true;
            this.splitContainer2.Location = new System.Drawing.Point(0, 0);
            this.splitContainer2.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.splitContainer2.Name = "splitContainer2";
            this.splitContainer2.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer2.Panel1
            // 
            this.splitContainer2.Panel1.Controls.Add(this.treeViewFast1);
            // 
            // splitContainer2.Panel2
            // 
            this.splitContainer2.Panel2.Controls.Add(this.btnWorldBrowserHelp);
            this.splitContainer2.Panel2.Controls.Add(this.btnStopRender);
            this.splitContainer2.Panel2.Controls.Add(this.btnDataCollapse);
            this.splitContainer2.Panel2.Controls.Add(this.btnToggleDBO);
            this.splitContainer2.Size = new System.Drawing.Size(350, 1041);
            this.splitContainer2.SplitterDistance = 961;
            this.splitContainer2.SplitterWidth = 5;
            this.splitContainer2.TabIndex = 0;
            // 
            // treeViewFast1
            // 
            this.treeViewFast1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.treeViewFast1.Enabled = false;
            this.treeViewFast1.ImageIndex = 0;
            this.treeViewFast1.ImageList = this.imageList1;
            this.treeViewFast1.Location = new System.Drawing.Point(4, 3);
            this.treeViewFast1.Margin = new System.Windows.Forms.Padding(0);
            this.treeViewFast1.Name = "treeViewFast1";
            this.treeViewFast1.SelectedImageIndex = 0;
            this.treeViewFast1.Size = new System.Drawing.Size(338, 949);
            this.treeViewFast1.TabIndex = 0;
            this.treeViewFast1.AfterSelect += new System.Windows.Forms.TreeViewEventHandler(this.TreeViewFast1_AfterSelect);
            // 
            // imageList1
            // 
            this.imageList1.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageList1.ImageStream")));
            this.imageList1.TransparentColor = System.Drawing.Color.Transparent;
            this.imageList1.Images.SetKeyName(0, "COMPUTER.ICO");
            this.imageList1.Images.SetKeyName(1, "Folder.ico");
            this.imageList1.Images.SetKeyName(2, "textdoc.ico");
            // 
            // btnWorldBrowserHelp
            // 
            this.btnWorldBrowserHelp.Cursor = System.Windows.Forms.Cursors.Default;
            this.btnWorldBrowserHelp.Location = new System.Drawing.Point(6, 6);
            this.btnWorldBrowserHelp.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnWorldBrowserHelp.Name = "btnWorldBrowserHelp";
            this.btnWorldBrowserHelp.Size = new System.Drawing.Size(125, 27);
            this.btnWorldBrowserHelp.TabIndex = 4;
            this.btnWorldBrowserHelp.Text = "Help";
            this.btnWorldBrowserHelp.Click += new System.EventHandler(this.BtnWorldBrowserHelp_Click);
            // 
            // btnStopRender
            // 
            this.btnStopRender.Cursor = System.Windows.Forms.Cursors.Default;
            this.btnStopRender.Enabled = false;
            this.btnStopRender.Location = new System.Drawing.Point(6, 40);
            this.btnStopRender.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnStopRender.Name = "btnStopRender";
            this.btnStopRender.Size = new System.Drawing.Size(126, 27);
            this.btnStopRender.TabIndex = 2;
            this.btnStopRender.Text = "Stop Render";
            this.btnStopRender.UseVisualStyleBackColor = true;
            this.btnStopRender.Click += new System.EventHandler(this.BtnStopRender_Click);
            // 
            // btnDataCollapse
            // 
            this.btnDataCollapse.Cursor = System.Windows.Forms.Cursors.Default;
            this.btnDataCollapse.Location = new System.Drawing.Point(139, 6);
            this.btnDataCollapse.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnDataCollapse.Name = "btnDataCollapse";
            this.btnDataCollapse.Size = new System.Drawing.Size(126, 27);
            this.btnDataCollapse.TabIndex = 3;
            this.btnDataCollapse.Text = "Show Data Viewer";
            this.btnDataCollapse.UseVisualStyleBackColor = true;
            this.btnDataCollapse.Click += new System.EventHandler(this.BtnDataCollapse_Click);
            // 
            // btnToggleDBO
            // 
            this.btnToggleDBO.Cursor = System.Windows.Forms.Cursors.Default;
            this.btnToggleDBO.Location = new System.Drawing.Point(139, 40);
            this.btnToggleDBO.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnToggleDBO.Name = "btnToggleDBO";
            this.btnToggleDBO.Size = new System.Drawing.Size(126, 27);
            this.btnToggleDBO.TabIndex = 3;
            this.btnToggleDBO.Text = "Show DBO";
            this.btnToggleDBO.UseVisualStyleBackColor = true;
            this.btnToggleDBO.Click += new System.EventHandler(this.BtnToggleDBO_Click);
            // 
            // splitContainer3
            // 
            this.splitContainer3.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.splitContainer3.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
            this.splitContainer3.Location = new System.Drawing.Point(4, 3);
            this.splitContainer3.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.splitContainer3.Name = "splitContainer3";
            // 
            // splitContainer3.Panel1
            // 
            this.splitContainer3.Panel1.Controls.Add(this.renderPanel);
            // 
            // splitContainer3.Panel2
            // 
            this.splitContainer3.Panel2.Controls.Add(this.splitContainer4);
            this.splitContainer3.Panel2Collapsed = true;
            this.splitContainer3.Size = new System.Drawing.Size(1540, 1005);
            this.splitContainer3.SplitterDistance = 669;
            this.splitContainer3.SplitterWidth = 5;
            this.splitContainer3.TabIndex = 2;
            // 
            // renderPanel
            // 
            this.renderPanel.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.renderPanel.Controls.Add(this.pictureBox1);
            this.renderPanel.Cursor = System.Windows.Forms.Cursors.Default;
            this.renderPanel.Location = new System.Drawing.Point(0, 0);
            this.renderPanel.Margin = new System.Windows.Forms.Padding(0);
            this.renderPanel.Name = "renderPanel";
            this.renderPanel.Size = new System.Drawing.Size(1535, 1005);
            this.renderPanel.TabIndex = 0;
            this.renderPanel.MouseHover += new System.EventHandler(this.RenderPanel_MouseHover);
            this.renderPanel.Resize += new System.EventHandler(this.RenderPanel_Resize);
            // 
            // pictureBox1
            // 
            this.pictureBox1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pictureBox1.BackColor = System.Drawing.Color.White;
            this.pictureBox1.Image = global::PugTools.Properties.Resources.LoadingSwirl;
            this.pictureBox1.Location = new System.Drawing.Point(0, 0);
            this.pictureBox1.Margin = new System.Windows.Forms.Padding(0);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(1535, 1005);
            this.pictureBox1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.CenterImage;
            this.pictureBox1.TabIndex = 0;
            this.pictureBox1.TabStop = false;
            // 
            // splitContainer4
            // 
            this.splitContainer4.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
            this.splitContainer4.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer4.Location = new System.Drawing.Point(0, 0);
            this.splitContainer4.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.splitContainer4.Name = "splitContainer4";
            this.splitContainer4.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer4.Panel1
            // 
            this.splitContainer4.Panel1.Controls.Add(this.tvfDataViewer);
            // 
            // splitContainer4.Panel2
            // 
            this.splitContainer4.Panel2.Controls.Add(this.dgvDataViewer);
            this.splitContainer4.Size = new System.Drawing.Size(96, 100);
            this.splitContainer4.SplitterDistance = 70;
            this.splitContainer4.SplitterWidth = 5;
            this.splitContainer4.TabIndex = 0;
            // 
            // tvfDataViewer
            // 
            this.tvfDataViewer.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tvfDataViewer.Enabled = false;
            this.tvfDataViewer.Location = new System.Drawing.Point(4, 3);
            this.tvfDataViewer.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tvfDataViewer.Name = "tvfDataViewer";
            this.tvfDataViewer.Size = new System.Drawing.Size(88, 59);
            this.tvfDataViewer.TabIndex = 0;
            this.tvfDataViewer.AfterSelect += new System.Windows.Forms.TreeViewEventHandler(this.TvfDataViewer_AfterSelect);
            this.tvfDataViewer.MouseUp += new System.Windows.Forms.MouseEventHandler(this.TvfDataViewer_MouseUp);
            // 
            // dgvDataViewer
            // 
            this.dgvDataViewer.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.dgvDataViewer.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgvDataViewer.Cursor = System.Windows.Forms.Cursors.Default;
            this.dgvDataViewer.Enabled = false;
            this.dgvDataViewer.Location = new System.Drawing.Point(4, 3);
            this.dgvDataViewer.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.dgvDataViewer.Name = "dgvDataViewer";
            this.dgvDataViewer.Size = new System.Drawing.Size(89, 13);
            this.dgvDataViewer.TabIndex = 0;
            // 
            // statusStrip1
            // 
            this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripStatusLabel1,
            this.toolStripProgressBar1});
            this.statusStrip1.Location = new System.Drawing.Point(0, 1013);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Padding = new System.Windows.Forms.Padding(1, 0, 16, 0);
            this.statusStrip1.Size = new System.Drawing.Size(1545, 24);
            this.statusStrip1.TabIndex = 1;
            this.statusStrip1.Text = "statusStrip1";
            // 
            // toolStripStatusLabel1
            // 
            this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            this.toolStripStatusLabel1.Size = new System.Drawing.Size(0, 19);
            // 
            // toolStripProgressBar1
            // 
            this.toolStripProgressBar1.ForeColor = System.Drawing.Color.Lime;
            this.toolStripProgressBar1.Name = "toolStripProgressBar1";
            this.toolStripProgressBar1.Size = new System.Drawing.Size(117, 18);
            this.toolStripProgressBar1.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            // 
            // backgroundWorker1
            // 
            this.backgroundWorker1.DoWork += new System.ComponentModel.DoWorkEventHandler(this.BackgroundWorker1_DoWork);
            this.backgroundWorker1.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.BackgroundWorker1_RunWorkerCompleted);
            // 
            // contextMenuStrip1
            // 
            this.contextMenuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripMenuItemViewAll});
            this.contextMenuStrip1.Name = "contextMenuStrip1";
            this.contextMenuStrip1.Size = new System.Drawing.Size(149, 26);
            // 
            // toolStripMenuItemViewAll
            // 
            this.toolStripMenuItemViewAll.Name = "toolStripMenuItemViewAll";
            this.toolStripMenuItemViewAll.Size = new System.Drawing.Size(148, 22);
            this.toolStripMenuItemViewAll.Text = "View All Items";
            // 
            // contextMenuStrip2
            // 
            this.contextMenuStrip2.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripMenuItem1});
            this.contextMenuStrip2.Name = "contextMenuStrip2";
            this.contextMenuStrip2.Size = new System.Drawing.Size(150, 26);
            // 
            // toolStripMenuItem1
            // 
            this.toolStripMenuItem1.Name = "toolStripMenuItem1";
            this.toolStripMenuItem1.Size = new System.Drawing.Size(149, 22);
            this.toolStripMenuItem1.Text = "Toggle Render";
            this.toolStripMenuItem1.Click += new System.EventHandler(this.ToolStripMenuItem1_Click);
            // 
            // BodyTypeStrip
            // 
            this.BodyTypeStrip.Name = "contextMenuStrip3";
            this.BodyTypeStrip.Size = new System.Drawing.Size(61, 4);
            // 
            // WorldBrowser
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1904, 1041);
            this.Controls.Add(this.splitContainer1);
            this.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.Name = "WorldBrowser";
            this.Text = "World Browser";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.WorldBrowser_FormClosing);
            this.splitContainer1.Panel1.ResumeLayout(false);
            this.splitContainer1.Panel2.ResumeLayout(false);
            this.splitContainer1.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).EndInit();
            this.splitContainer1.ResumeLayout(false);
            this.splitContainer2.Panel1.ResumeLayout(false);
            this.splitContainer2.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).EndInit();
            this.splitContainer2.ResumeLayout(false);
            this.splitContainer3.Panel1.ResumeLayout(false);
            this.splitContainer3.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).EndInit();
            this.splitContainer3.ResumeLayout(false);
            this.renderPanel.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.splitContainer4.Panel1.ResumeLayout(false);
            this.splitContainer4.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer4)).EndInit();
            this.splitContainer4.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvDataViewer)).EndInit();
            this.statusStrip1.ResumeLayout(false);
            this.statusStrip1.PerformLayout();
            this.contextMenuStrip1.ResumeLayout(false);
            this.contextMenuStrip2.ResumeLayout(false);
            this.ResumeLayout(false);

    }

    #endregion

    private System.Windows.Forms.SplitContainer splitContainer1;
    private TreeViewFast.Controls.TreeViewFast treeViewFast1;
    private System.Windows.Forms.Panel renderPanel;
    private System.ComponentModel.BackgroundWorker backgroundWorker1;
    private System.Windows.Forms.StatusStrip statusStrip1;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel1;
    private System.Windows.Forms.ToolStripProgressBar toolStripProgressBar1;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
    private System.Windows.Forms.ToolStripMenuItem toolStripMenuItemViewAll;
    private System.Windows.Forms.SplitContainer splitContainer2;
    private System.Windows.Forms.Button btnWorldBrowserHelp;
    private System.Windows.Forms.Button btnStopRender;
    private System.Windows.Forms.Button btnDataCollapse;
    private System.Windows.Forms.Button btnToggleDBO;
    private System.Windows.Forms.SplitContainer splitContainer3;
    private System.Windows.Forms.SplitContainer splitContainer4;
    private TreeViewFast.Controls.TreeViewFast tvfDataViewer;
    private System.Windows.Forms.DataGridView dgvDataViewer;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip2;
    private System.Windows.Forms.ToolStripMenuItem toolStripMenuItem1;
    private System.Windows.Forms.PictureBox pictureBox1;
    private System.Windows.Forms.ContextMenuStrip BodyTypeStrip;
    private System.Windows.Forms.ImageList imageList1;
  }
}
