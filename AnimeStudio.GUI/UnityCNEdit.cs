using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace AnimeStudio.GUI
{
    public partial class UnityCNEdit : Form
    {
        public UnityCNEdit()
        {
            InitializeComponent();

            updateDisplay();
        }

        private void updateDisplay()
        {
            dataGridView1.Rows.Clear();
            var keys = UnityCNManager.GetKeys();

            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var rowIdx = dataGridView1.Rows.Add();

                dataGridView1.Rows[rowIdx].Cells[0].Value = key[0];
                dataGridView1.Rows[rowIdx].Cells[1].Value = key[1];
                dataGridView1.Rows[rowIdx].Cells[2].Value = key[2];
            }
        }

        private void saveBtn_Click(object sender, EventArgs e)
        {
            var keys = new List<List<string>>();

            foreach (DataGridViewRow row in dataGridView1.Rows)
            {
                if (row.IsNewRow) continue;

                var entry = new List<string>
                {
                    row.Cells[0].Value?.ToString() ?? "",
                    row.Cells[1].Value?.ToString() ?? "",
                    row.Cells[2].Value?.ToString() ?? ""
                };

                keys.Add(entry);
            }

            Logger.Info("[UnityCN] Saved keys !!");

            UnityCNManager.SetKeys(keys);
            GameManager.ReloadGames();
            DialogResult = DialogResult.OK;
            this.Close();
        }

        private void resetBtn_Click(object sender, EventArgs e)
        {
            var keys = UnityCNManager.GetDefaultKeys();
            UnityCNManager.SetKeys(keys);
            updateDisplay();
        }
    }
}
