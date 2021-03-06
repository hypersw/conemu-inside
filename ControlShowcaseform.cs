﻿using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

using ConEmu.WinForms;

namespace ConEmuInside
{
	public class ControlShowcaseForm : Form
	{
		public ControlShowcaseForm()
		{
			Size = new Size(800, 600);

			ConEmuControl conemu;
			Controls.Add(conemu = new ConEmuControl() {Dock = DockStyle.Fill, MinimumSize = new Size(200, 200), IsStatusbarVisible = true});
			if(conemu.AutoStartInfo != null)
			{
				conemu.AutoStartInfo.SetEnv("one", "two");
				conemu.AutoStartInfo.SetEnv("geet", "huub");
			}
			//conemu.AutoStartInfo = null;
			TextBox txt;
			Controls.Add(txt = new TextBox() {Text = "AnotherFocusableControl", AutoSize = true, Dock = DockStyle.Top});

			FlowLayoutPanel stack;
			Controls.Add(stack = new FlowLayoutPanel() {FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink});

			Button btn;
			stack.Controls.Add(btn = new Button() {Text = "Exec Command", AutoSize = true, Dock = DockStyle.Left});
			btn.Click += delegate { conemu.PasteText("whois microsoft.com" + Environment.NewLine); };

			stack.Controls.Add(btn = new Button() {Text = "Query HWND", AutoSize = true, Dock = DockStyle.Left});
			btn.Click += delegate { conemu.BeginGuiMacro("GetInfo").WithParam("HWND").Execute(result => txt.Text = $"ConEmu HWND: {Regex.Replace(result.Response, "\\s+", " ")}"); };

			stack.Controls.Add(btn = new Button() {Text = "Query PID", AutoSize = true, Dock = DockStyle.Left});
			btn.Click += delegate { conemu.BeginGuiMacro("GetInfo").WithParam("PID").Execute(result => txt.Text = $"ConEmu PID: {Regex.Replace(result.Response, "\\s+", " ")}"); };

			CheckBox checkStatusBar;
			stack.Controls.Add(checkStatusBar = new CheckBox() {Text = "StatusBar", Checked = conemu.IsStatusbarVisible});
			checkStatusBar.CheckedChanged += delegate { conemu.IsStatusbarVisible = checkStatusBar.Checked; };

			TextBox txtOutput = null;

			stack.Controls.Add(btn = new Button() {Text = "&Ping", AutoSize = true, Dock = DockStyle.Left});
			btn.Click += delegate
			{
				if(conemu.IsConsoleProcessRunning)
				{
					MessageBox.Show(this, "The console is busy right now.", "Ping", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				if(txtOutput == null)
					Controls.Add(txtOutput = new TextBox() {Multiline = true, Dock = DockStyle.Right, Width = 200});
				conemu.Start(new ConEmuStartInfo() {ConsoleCommandLine = "ping ya.ru", AnsiStreamChunkReceivedEventSink = (sender, args) => txtOutput.Text += args.GetMbcsText(), WhenPayloadProcessExits = WhenPayloadProcessExits.KeepTerminalAndShowMessage, PayloadExitedEventSink = (sender, args) => txtOutput.Text += $"Exited with ERRORLEVEL {args.ExitCode}."});
			};

			stack.Controls.Add(btn = new Button() {Text = "&Choice", AutoSize = true, Dock = DockStyle.Left});
			btn.Click += delegate
			{
				conemu.RunningSession?.KillConsoleEmulator();
				DialogResult result = MessageBox.Show(this, "Keep terminal when payload exits?", "Choice", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
				if(result == DialogResult.Cancel)
					return;
				conemu.Start(new ConEmuStartInfo() {ConsoleCommandLine = "choice", WhenPayloadProcessExits = result == DialogResult.Yes ? WhenPayloadProcessExits.KeepTerminalAndShowMessage : WhenPayloadProcessExits.CloseTerminal, PayloadExitedEventSink = (sender, args) => MessageBox.Show(this, $"Your choice is {args.ExitCode}.", "Choice", MessageBoxButtons.OK, MessageBoxIcon.Information)});
			};
		}
	}
}