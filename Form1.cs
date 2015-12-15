﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;

namespace ConEmuInside
{
    public partial class ChildTerminal : Form
    {
        protected Process ConEmu;

        public ChildTerminal()
        {
            InitializeComponent();
        }

        private void ChildTerminal_Load(object sender, EventArgs e)
        {
            string lsOurDir;
            argConEmuExe.Text = GetConEmu();
            argDirectory.Text = Directory.GetCurrentDirectory();
            lsOurDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            argXmlFile.Text = Path.Combine(lsOurDir, "ConEmu.xml");
            argCmdLine.Text = @"{cmd}"; // Use ConEmu's default {cmd} task
            RefreshControls(false);
            // Force focus to ‘cmd line’ control
            argCmdLine.Select();
            //
            termPanel.Resize += new System.EventHandler(this.termPanel_Resize);
        }

        private void RefreshControls(bool bTermActive)
        {
            if (bTermActive)
            {
                AcceptButton = null;
                groupBox2.Enabled = true;
                if (startPanel.Visible)
                {
                    promptBox.Focus();
                    startPanel.Visible = false;
                }
                if (!termPanel.Visible)
                {
                    termPanel.Visible = true;
                }
            }
            else
            {
                if (termPanel.Visible)
                {
                    termPanel.Visible = false;
                }
                if (!startPanel.Visible)
                {
                    startPanel.Visible = true;
                    argCmdLine.Focus();
                }
                groupBox2.Enabled = false;
                AcceptButton = startBtn;
            }


        }

        private string GetConEmu()
        {
            string sOurDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            string[] sSearchIn = {
              Directory.GetCurrentDirectory(),
              sOurDir,
              Path.Combine(sOurDir, ".."),
              Path.Combine(sOurDir, "ConEmu"),
              "%PATH%", "%REG%"
              };

            string[] sNames;
            sNames = new string[] { "ConEmu.exe", "ConEmu64.exe" };

            foreach (string sd in sSearchIn)
            {
                foreach (string sn in sNames)
                {
                    string spath;
                    if (sd == "%PATH%" || sd == "%REG%")
                    {
                        spath = sn; //TODO
                    }
                    else
                    {
                        spath = Path.Combine(sd, sn);
                    }
                    if (File.Exists(spath))
                        return spath;
                }
            }

            // Default
            return "ConEmu.exe";
        }

        private string GetConEmuC()
        {
            // Returns Path to ConEmuC (to GuiMacro execution)

            if ((ConEmu == null) || ConEmu.HasExited)
                return null;
            if (ConEmu.Modules.Count == 0)
                return null;

            String lsExeDir, ConEmuC;
            lsExeDir = Path.GetDirectoryName(ConEmu.Modules[0].FileName);
            ConEmuC = Path.Combine(lsExeDir, @"ConEmu\ConEmuC.exe");
            if (!File.Exists(ConEmuC))
            {
                ConEmuC = Path.Combine(lsExeDir, @"ConEmuC.exe");
                if (!File.Exists(ConEmuC))
                {
                    ConEmuC = "ConEmuC.exe"; // Must not get here actually
                }
            }
            return ConEmuC;
        }

        private void ExecuteGuiMacro(string asMacro)
        {
            // conemuc.exe -silent -guimacro:1234 print("\e","git"," --version","\n")
            string ConEmuC = GetConEmuC();
            if (ConEmuC != null)
            {
                ProcessStartInfo macro = new ProcessStartInfo(
                   ConEmuC,
                   " -GuiMacro:" + ConEmu.Id.ToString() +
                     " " +
                     asMacro
                    );
                macro.WindowStyle = ProcessWindowStyle.Hidden;
                macro.CreateNoWindow = true;
                Process.Start(macro);
            }
        }

        private void printBtn_Click(object sender, EventArgs e)
        {
            if (promptBox.Text == "")
                return;
            String lsMacro;
            lsMacro = "Print(@\"" + promptBox.Text.Replace("\"", "\"\"") + "\",\"\n\")";
            ExecuteGuiMacro(lsMacro);
            promptBox.SelectAll();
        }

        private void macroBtn_Click(object sender, EventArgs e)
        {
            if (promptBox.Text == "")
                return;
            ExecuteGuiMacro(promptBox.Text);
            promptBox.SelectAll();
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if ((ConEmu != null) && ConEmu.HasExited)
            {
                timer1.Stop();
                ConEmu = null;
                RefreshControls(false);
            }
        }

        private void promptBox_KeyDown(object sender, KeyEventArgs e)
        {
            //TODO: Enter and Leave events are not triggered when focus is put into ConEmu window
            if (e.KeyValue == 13)
            {
                printBtn_Click(sender, null);
            }
        }

        private void promptBox_Enter(object sender, EventArgs e)
        {
            //TODO: Enter and Leave events are not triggered when focus is put into ConEmu window
            //AcceptButton = printBtn;
            //promptBox.Text = "...in...";
        }

        private void promptBox_Leave(object sender, EventArgs e)
        {
            //TODO: Enter and Leave events are not triggered when focus is put into ConEmu window
            //if (AcceptButton == printBtn)
            //AcceptButton = null;
            //promptBox.Text = "...out...";
        }

        private void exeBtn_Click(object sender, EventArgs e)
        {
            openFileDialog1.Title = "Choose ConEmu main executable";
            openFileDialog1.FileName = argConEmuExe.Text;
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                argConEmuExe.Text = openFileDialog1.FileName;
            }
            argConEmuExe.Focus();
        }

        private void cmdBtn_Click(object sender, EventArgs e)
        {
            openFileDialog1.Title = "Choose startup shell";
            openFileDialog1.FileName = "";
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                argCmdLine.Text = openFileDialog1.FileName;
            }
            argCmdLine.Focus();
        }

        private void dirBtn_Click(object sender, EventArgs e)
        {
            folderBrowserDialog1.SelectedPath = argDirectory.Text;
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
            {
                argDirectory.Text = folderBrowserDialog1.SelectedPath;
            }
            argDirectory.Focus();
        }

        private void startBtn_Click(object sender, EventArgs e)
        {
            string sRunAs, sRunArgs;

            // Show terminal panel, hide start options
            //RefreshControls(true);

            sRunAs = argRunAs.Checked ? " -cur_console:a" : "";

            sRunArgs =
                (argDebug.Checked ? " -debugw" : "") +
                " -NoKeyHooks" +
                " -InsideWnd 0x" + termPanel.Handle.ToString("X") +
                " -LoadCfgFile \"" + argXmlFile.Text + "\"" +
                " -Dir \"" + argDirectory.Text + "\"" +
                (argLog.Checked ? " -Log" : "") +
                " -cmd " + // This one MUST be the last switch
                argCmdLine.Text + sRunAs // And the shell command line itself
                ;

            try {
                // Start ConEmu
                ConEmu = Process.Start(argConEmuExe.Text, sRunArgs);
                RefreshControls(true);
                // Start monitoring
                timer1.Start();
            } catch (System.ComponentModel.Win32Exception ex) {
                RefreshControls(false);
                MessageBox.Show(ex.Message + "\r\n\r\n" +
                    "Command:\r\n" + argConEmuExe.Text + "\r\n\r\n" +
                    "Arguments:\r\n" + sRunArgs,
                    ex.GetType().FullName + " (" + ex.NativeErrorCode.ToString() + ")",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void startArgs_Enter(object sender, EventArgs e)
        {
            AcceptButton = startBtn;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr FindWindowEx(IntPtr hParent, IntPtr hChild, string szClass, string szWindow);

        private void termPanel_Resize(object sender, EventArgs e)
        {
            if (ConEmu != null)
            {
                IntPtr hConEmu = FindWindowEx(termPanel.Handle, (IntPtr)0, null, null);
                if (hConEmu != (IntPtr)0)
                {
                    MoveWindow(hConEmu, 0, 0, termPanel.Width, termPanel.Height, true);
                }
            }
        }
    }
}
