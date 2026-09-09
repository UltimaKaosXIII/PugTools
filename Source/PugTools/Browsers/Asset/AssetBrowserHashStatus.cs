using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows.Forms;
using TorArchive;

namespace PugTools {
  internal partial class AssetBrowserHashStatus : Form {
    private Assets currentAssets;
    private Double dblAllCompletion = 0;
    private DataTable dt = new DataTable();
    private Int32 intAllTotal = 0;
    private Int32 intAllNamed = 0;
    private Int32 intAllMissing = 0;

    internal AssetBrowserHashStatus() {
      InitializeComponent();
    }
    private void AssetBrowserHashStatus_Load(Object sender, EventArgs e) {
      currentAssets = AssetHandler.Instance.GetCurrentAssets();
      loadingSwirl1.Visible = true;
      dgvHashStatus.Enabled = false;
      toolStripProgressBar1.Visible = true;
      toolStripStatusLabel1.Text = "Calculating Hash Status...";

      Refresh();

      backgroundWorker1.RunWorkerAsync();
    }
    private void BackgroundWorker1_DoWork(Object sender, DoWorkEventArgs e) {
      dt = new DataTable();

      dt.Columns.Add("Archive", typeof(String));
      dt.Columns.Add("Total Files", typeof(Int32));
      dt.Columns.Add("Named Files", typeof(Int32));
      dt.Columns.Add("Missing Files", typeof(Int32));
      dt.Columns.Add("Completion", typeof(Double));

      foreach (Library lib in currentAssets.Libraries) {
        foreach (KeyValuePair<Int32, Archive> arch in lib.Archives) {
          String archName = arch.Value.FileName.Split('\\').Last();
          Int32 intTotal = 0;
          Int32 intNamed = 0;

          foreach (File file in arch.Value.EnumerateFiles()) {
            intTotal++;
            intAllTotal++;
            HashFileInfo hashInfo =
              new HashFileInfo(file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash, file, true, false);

            if (hashInfo.IsNamed) {
              intNamed++;
              intAllNamed++;
            }
          }

          Int32 intMissing = intTotal - intNamed;
          Double dblCompletion = intNamed / (Double)intTotal;
          DataRow row = dt.NewRow();

          row[0] = archName;
          row[1] = intTotal;
          row[2] = intNamed;
          row[3] = intMissing;
          row[4] = dblCompletion;

          dt.Rows.Add(row);
          // dt.Rows.Add(new string[] { archName, 
          //   String.Format("{0:n0}", intTotal), 
          //   String.Format("{0:n0}", intNamed), 
          //   String.Format("{0:n0}", intMissing), 
          //   String.Format("{0:0.0%}", dblCompletion) 
          //   }
          // );
        }
      }
    }
    private void BackgroundWorker1_RunWorkerCompleted(Object sender,
                                                      RunWorkerCompletedEventArgs e) {
      dgvHashStatus.DataSource = dt;
      dgvHashStatus.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
      dgvHashStatus.Enabled = true;
      dgvHashStatus.Columns["Total Files"].DefaultCellStyle.Format = "0";
      dgvHashStatus.Columns["Named Files"].DefaultCellStyle.Format = "0";
      dgvHashStatus.Columns["Missing Files"].DefaultCellStyle.Format = "0";
      dgvHashStatus.Columns["Completion"].DefaultCellStyle.Format = "0.0%";

      intAllMissing = intAllTotal - intAllNamed;
      dblAllCompletion = intAllNamed / (Double)intAllTotal;

      lblTotalFilesVal.Text = String.Format("{0:n0}", intAllTotal);
      lblTotalNamedVal.Text = String.Format("{0:n0}", intAllNamed);
      lblTotalMissingVal.Text = String.Format("{0:n0}", intAllMissing);
      lblCompletionVal.Text = String.Format("{0:0.0%}", dblAllCompletion);

      lblTotalFilesVal.Visible = true;
      lblTotalNamedVal.Visible = true;
      lblTotalMissingVal.Visible = true;
      lblCompletionVal.Visible = true;

      loadingSwirl1.Visible = false;
      toolStripProgressBar1.Visible = false;
      toolStripStatusLabel1.Text = "Complete";
    }
  }
}
