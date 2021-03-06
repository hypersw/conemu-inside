﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

using JetBrains.Annotations;

using Microsoft.Build.Utilities;

namespace ConEmu.WinForms
{
	/// <summary>
	/// A single session of the console emulator run. Each top-level command execution spawns a new session.
	/// </summary>
	public unsafe class ConEmuSession
	{
		static readonly bool IsExecutingGuiMacrosInProcess = true;

		[NotNull]
		private readonly DirectoryInfo _dirLocalTempRoot;

		[NotNull]
		private readonly WindowsFormsSynchronizationContext _dispatcher;

		bool _isPayloadExited;

		/// <summary>
		/// The exit code of the payload process, if it has already exited (<see cref="_isPayloadExited" />). Otherwise, undefined behavior.
		/// </summary>
		private int _nPayloadExitCode;

		/// <summary>
		/// The ConEmu process, even after it exits.
		/// </summary>
		[NotNull]
		private readonly Process _process;

		[NotNull]
		private readonly ConEmuStartInfo _startinfo;

		public ConEmuSession([NotNull] ConEmuStartInfo startinfo, [NotNull] HostContext hostcontext)
		{
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(hostcontext == null)
				throw new ArgumentNullException(nameof(hostcontext));
			_startinfo = startinfo;
			startinfo.MarkAsUsedUp();
			if(string.IsNullOrEmpty(startinfo.ConsoleCommandLine))
				throw new InvalidOperationException($"Cannot start a new console process for command line “{startinfo.ConsoleCommandLine}” because it's either NULL, or empty, or whitespace.");

			// Global environment
			_dispatcher = new WindowsFormsSynchronizationContext();
			_dirLocalTempRoot = new DirectoryInfo(Path.Combine(Path.Combine(Path.GetTempPath(), "ConEmu"), $"{DateTime.UtcNow.ToString("s").Replace(':', '-')}.{Process.GetCurrentProcess().Id:X8}.{unchecked((uint)RuntimeHelpers.GetHashCode(this)):X8}")); // Prefixed with date-sortable; then PID; then sync table id of this object

			// Events wiring: make sure sinks pre-installed with start-info also get notified
			if(startinfo.PayloadExitedEventSink != null)
				PayloadExited += startinfo.PayloadExitedEventSink;
			if(startinfo.ConsoleEmulatorExitedEventSink != null)
				ConsoleEmulatorExited += startinfo.ConsoleEmulatorExitedEventSink;

			// Should feed ANSI log?
			if(startinfo.IsReadingAnsiStream)
			{
				_ansilog = new AnsiLog(_dirLocalTempRoot);
				if(startinfo.AnsiStreamChunkReceivedEventSink != null)
					_ansilog.AnsiStreamChunkReceived += startinfo.AnsiStreamChunkReceivedEventSink;

				// Do the pumping periodically (TODO: take this to async?.. but would like to keep the final evt on the home thread, unless we go to tasks)
				// TODO: if ConEmu writes to a pipe, we might be getting events when more data comes to the pipe rather than poll it by timer
				_evtPolling += delegate { _ansilog.PumpStream(); };

				// Final
				_evtCleanupOnExit += delegate { _ansilog.Dispose(); };
			}

			// Cmdline
			CommandLineBuilder cmdl = Init_MakeConEmuCommandLine(startinfo, hostcontext, _ansilog, _dirLocalTempRoot);

			// Start ConEmu
			try
			{
				if(string.IsNullOrEmpty(startinfo.ConEmuExecutablePath))
					throw new InvalidOperationException("Could not run the console emulator. The path to ConEmu.exe could not be detected.");
				if(!File.Exists(startinfo.ConEmuExecutablePath))
					throw new InvalidOperationException($"Missing ConEmu executable at location “{startinfo.ConEmuExecutablePath}”.");
				var processNew = new Process() {StartInfo = new ProcessStartInfo(startinfo.ConEmuExecutablePath, cmdl.ToString()) {UseShellExecute = false}};

				// Bind process termination
				processNew.EnableRaisingEvents = true;
				processNew.Exited += delegate
				{
					_dispatcher.Post(o => // Ensure STA
					{
#pragma warning disable once AccessToModifiedClosure
						_evtCleanupOnExit?.Invoke(this, EventArgs.Empty);

						// If we haven't separately caught an exit of the payload process
						if(!_isPayloadExited)
						{
							_isPayloadExited = true;

							// We haven't caught the exit of the payload process, so we haven't gotten a message with its errorlevel as well. Assume ConEmu propagates its exit code, as there ain't other way for getting it now
							// TODO: check that this assumption holds
							_nPayloadExitCode = _process.ExitCode;

							_ansilog?.Dispose(); // Just to make sure it's been pumped out (should have been by evtCleanupOnExit)
							PayloadExited?.Invoke(this, new ProcessExitedEventArgs(_nPayloadExitCode));
						}

						// All exited now
						ConsoleEmulatorExited?.Invoke(this, EventArgs.Empty);
					}, null);
				};

				if(!processNew.Start())
					throw new Win32Exception("The process did not start.");
				_process = processNew;
			}
			catch(Win32Exception ex)
			{
				throw new InvalidOperationException("Could not run the console emulator. " + ex.Message + $" ({ex.NativeErrorCode:X8})" + Environment.NewLine + Environment.NewLine + "Command:" + Environment.NewLine + startinfo.ConEmuExecutablePath + Environment.NewLine + Environment.NewLine + "Arguments:" + Environment.NewLine + cmdl, ex);
			}

			// All evt* multicast events are condidered noncritical up to this point, so if the process fails to start, no polling/cleanup will be fired yet (and user gets notified via an exception)
			// From this point on, we MUST guarantee reliable firing of events

			// GuiMacro executor & cleanup
			_guiMacroExecutor = new GuiMacroExecutor(startinfo.ConEmuConsoleServerExecutablePath);
			_evtCleanupOnExit += delegate { ((IDisposable)_guiMacroExecutor).Dispose(); };

			// Monitor payload process
			Init_PayloadProcessMonitoring();

			///////////////////////////////
			// Set up the polling event
			// NOTE: for now, this will be a polling timer, consider free-threaded treatment later on
			var timer = new Timer() {Interval = (int)TimeSpan.FromSeconds(.1).TotalMilliseconds, Enabled = true};
			timer.Tick += delegate
			{
				_evtPolling?.Invoke(this, EventArgs.Empty);
				if(_process.HasExited)
				{
					_evtPolling = null; // Timer might fire a few more fake events on disposal
					timer.Dispose();
				}
			};
			_evtCleanupOnExit += delegate { timer.Dispose(); };

			// Schedule cleanup of temp files
			_evtCleanupOnExit += delegate
			{
				try
				{
					if(_dirLocalTempRoot.Exists)
						_dirLocalTempRoot.Delete(true);
				}
				catch(Exception)
				{
					// Not interested
				}
			};
		}

		/// <summary>
		/// Watches for the status of the payload process to fetch its exitcode when done and notify user of that.
		/// </summary>
		private void Init_PayloadProcessMonitoring()
		{
			var xmlDoc = new XmlDocument();

			// Detect when payload process exits
			Process processRoot = null; // After we know the root payload process PID, we'd wait for it to exit
			_evtCleanupOnExit += delegate
			{
				if(processRoot != null)
				{
					processRoot.Close();
					processRoot = null;
				}
			};
			_evtPolling += delegate
			{
				if(_isPayloadExited)
					return;
				if(_process.HasExited)
					return;

				// Know root already?
				if(processRoot != null)
				{
					if(!processRoot.HasExited)
						return; // We know the root, and it has not exited yet. No use in re-asking anything.

					processRoot.Close();
					processRoot = null; // Has exited => force a re-ask, we'd get its exit code from there
				}

				// Query on the root process
				if(processRoot == null)
				{
					try
					{
						// Get info on the root payload process
						GuiMacroResult result = BeginGuiMacro("GetInfo").WithParam("Root").ExecuteSync();

						if(result.ErrorLevel != 0) // E.g. ConEmu has not fully started yet, and ConEmuC failed to connect to the instance — would usually return an error on the first call
							return;
						if(string.IsNullOrEmpty(result.Response)) // Might yield an empty string randomly if not ready yet
							return;

						// Interpret the string as XML
						/*
						xmlDoc.LoadXml(result.Response);

						XmlElement xmlRoot = xmlDoc.DocumentElement;
						if(xmlRoot == null)
							return;

						Trace.WriteLine($"ROOT: {xmlRoot.OuterXml}");
*/

						// Current possible records:
						// <Root	"Name"="cmd.exe"/>
						// <Root	"Name"="cmd.exe"	"Running": true	"PID""6812"	"ExitCode""259"	"UpTime"="7735"/>
						// <Root	"Name"="cmd.exe"	"Running": false	"PID""6812"	"ExitCode""0"	"UpTime"="7751"/>
						// Interested of the latter two only

						Match match = Regex.Match(result.Response, @".*Running\W+(?<IsRunning>true|false).*PID\W+(?<Pid>\d+)(.*ExitCode\W+(?<ExitCode>-?\d+))?.*", RegexOptions.IgnoreCase);
						if(!match.Success)
							return;

						bool isRunning = bool.Parse(match.Groups["IsRunning"].Value);

						if(isRunning)
						{
							// Monitor a running process
							int nPid = int.Parse(match.Groups["Pid"].Value);
							processRoot = Process.GetProcessById(nPid);
							// Migth sink its Exited event (when we got Tasks here e.g.), but for now it's simpler to reuse the same polling timer
						}
						else // Means done running the payload process in ConEmu, all done
						{
							// Make sure the whole ANSI log contents is pumped out before we notify user
							// Dispose call pumps all out and makes sure we never ever fire anything on it after we notify user of PayloadExited; multiple calls to Dispose are OK
							_ansilog?.Dispose();

							// Fetch exit code
							if(match.Groups["ExitCode"].Success)
								_nPayloadExitCode = int.Parse(match.Groups["ExitCode"].Value);
							_isPayloadExited = true;

							// Notify user
							PayloadExited?.Invoke(this, new ProcessExitedEventArgs(_nPayloadExitCode));
						}
					}
					catch
					{
						// Don't want to break the whole process, and got no exception reporter here => skip nonfatal errors, assume it's a temporary problem, ask next time
					}
				}
			};
		}

		[NotNull]
		private static CommandLineBuilder Init_MakeConEmuCommandLine([NotNull] ConEmuStartInfo startinfo, [NotNull] HostContext hostcontext, [CanBeNull] AnsiLog ansilog, [NotNull] DirectoryInfo dirLocalTempRoot)
		{
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(hostcontext == null)
				throw new ArgumentNullException(nameof(hostcontext));

			var cmdl = new CommandLineBuilder();

			// This sets up hosting of ConEmu in our control
			cmdl.AppendSwitch("-InsideWnd");
			cmdl.AppendFileNameIfNotNull("0x" + ((ulong)hostcontext.HWndParent).ToString("X"));

			// Don't use keyboard hooks in ConEmu when embedded
			cmdl.AppendSwitch("-NoKeyHooks");

			// Basic settings, like fonts and hidden tab bar
			// Plus some of the properties on this class
			cmdl.AppendSwitch("-LoadCfgFile");
			cmdl.AppendFileNameIfNotNull(Init_MakeConEmuCommandLine_EmitConfigFile(dirLocalTempRoot, startinfo, hostcontext));

			if(!string.IsNullOrEmpty(startinfo.StartupDirectory))
			{
				cmdl.AppendSwitch("-Dir");
				cmdl.AppendFileNameIfNotNull(startinfo.StartupDirectory);
			}

			// ANSI Log file
			if(ansilog != null)
			{
				cmdl.AppendSwitch("-AnsiLog");
				cmdl.AppendFileNameIfNotNull(ansilog.Directory.FullName);
			}
			if(dirLocalTempRoot == null)
				throw new ArgumentNullException(nameof(dirLocalTempRoot));

			// This one MUST be the last switch
			cmdl.AppendSwitch("-cmd");

			// Console mode command
			// NOTE: if placed AFTER the payload command line, otherwise somehow conemu hooks won't fetch the switch out of the cmdline, e.g. with some complicated git fetch/push cmdline syntax which has a lot of colons inside on itself
			string sConsoleExitMode;
			switch(startinfo.WhenPayloadProcessExits)
			{
			case WhenPayloadProcessExits.CloseTerminal:
				sConsoleExitMode = "n";
				break;
			case WhenPayloadProcessExits.KeepTerminal:
				sConsoleExitMode = "c0";
				break;
			case WhenPayloadProcessExits.KeepTerminalAndShowMessage:
				sConsoleExitMode = "c";
				break;
			default:
				throw new ArgumentOutOfRangeException("ConEmuStartInfo" + "::" + "WhenPayloadProcessExits", startinfo.WhenPayloadProcessExits, "This is not a valid enum value.");
			}
			cmdl.AppendSwitchIfNotNull("-cur_console:", $"{(startinfo.IsElevated ? "a" : "")}{sConsoleExitMode}");

			// And the shell command line itself
			cmdl.AppendSwitch(startinfo.ConsoleCommandLine);

			return cmdl;
		}

		private static string Init_MakeConEmuCommandLine_EmitConfigFile([NotNull] DirectoryInfo dirForConfigFile, [NotNull] ConEmuStartInfo startinfo, [NotNull] HostContext hostcontext)
		{
			if(dirForConfigFile == null)
				throw new ArgumentNullException(nameof(dirForConfigFile));
			if(startinfo == null)
				throw new ArgumentNullException(nameof(startinfo));
			if(hostcontext == null)
				throw new ArgumentNullException(nameof(hostcontext));
			// Load default template
			var xmldoc = new XmlDocument();
			xmldoc.Load(new MemoryStream(Resources.ConEmuSettingsTemplate));
			XmlNode xmlSettings = xmldoc.SelectSingleNode("/key[@name='Software']/key[@name='ConEmu']/key[@name='.Vanilla']");
			if(xmlSettings == null)
				throw new InvalidOperationException("Unexpected mismatch in XML resource structure.");

			// Apply settings from properties
			{
				var xmlElem = ((XmlElement)(xmlSettings.SelectSingleNode("value[@name='StatusBar.Show']") ?? xmlSettings.AppendChild(xmldoc.CreateElement("value"))));
				xmlElem.SetAttribute("type", "hex");
				xmlElem.SetAttribute("data", (hostcontext.IsStatusbarVisibleInitial ? 1 : 0).ToString());
			}

			// Environment variables
			if(startinfo.EnumEnv().Any())
			{
				var xmlElem = ((XmlElement)(xmlSettings.SelectSingleNode("value[@name='EnvironmentSet']") ?? xmlSettings.AppendChild(xmldoc.CreateElement("value"))));
				xmlElem.SetAttribute("type", "multi");
				foreach(string key in startinfo.EnumEnv())
				{
					XmlElement xmlLine;
					xmlElem.AppendChild(xmlLine = xmldoc.CreateElement("line"));
					xmlLine.SetAttribute("data", $"set {key}={startinfo.GetEnv(key)}");
				}
			}

			// Write out to temp location
			dirForConfigFile.Create();
			string sConfigFile = Path.Combine(dirForConfigFile.FullName, "Config.Xml");
			xmldoc.Save(sConfigFile);

			return sConfigFile;
		}

		/// <summary>
		/// Gets the exit code of the payload process, if <see cref="IsPayloadExited">it has already exited</see>. Throws an exception if it has not.
		/// </summary>
		public int GetPayloadExitCode()
		{
			if(!_isPayloadExited)
				throw new InvalidOperationException("The exit code is not available yet because the process has not yet exited.");
			return _nPayloadExitCode;
		}

		/// <summary>
		/// Gets whether the payload process has already exited (see <see cref="PayloadExited" />). The console emulator process might have exited as well, but might have not.
		/// </summary>
		public bool IsPayloadExited => _isPayloadExited;

		/// <summary>
		/// Gets the start info with which this session has been started.
		/// </summary>
		[NotNull]
		public ConEmuStartInfo StartInfo
		{
			get
			{
				return _startinfo;
			}
		}

		/// <summary>
		/// Starts construction of the ConEmu GUI Macro.
		/// </summary>
		[Pure]
		public GuiMacroBuilder BeginGuiMacro([NotNull] string sMacroName)
		{
			if(sMacroName == null)
				throw new ArgumentNullException(nameof(sMacroName));

			return new GuiMacroBuilder(this, sMacroName, Enumerable.Empty<string>());
		}

		/// <summary>
		/// Executes a ConEmu GUI Macro on the active console, see http://conemu.github.io/en/GuiMacro.html .
		/// </summary>
		/// <param name="macrotext">The full macro command, see http://conemu.github.io/en/GuiMacro.html .</param>
		/// <param name="FWhenDone">Optional. Executes on the same thread when the macro is done executing.</param>
		/// <param name="isSync">Forces synchronous execution. If <c>False</c>, the operation might be carried out async.</param>
		public void ExecuteGuiMacroText([NotNull] string macrotext, [CanBeNull] Action<GuiMacroResult> FWhenDone = null, bool isSync = false)
		{
			if(macrotext == null)
				throw new ArgumentNullException(nameof(macrotext));

			Process processConEmu = _process;
			if(processConEmu == null)
				throw new InvalidOperationException("Cannot execute a macro because the console process is not running at the moment.");

			if(IsExecutingGuiMacrosInProcess)
			{
				GuiMacroResult result = _guiMacroExecutor.ExecuteInProcess(processConEmu.Id, macrotext);
				FWhenDone?.Invoke(result);
			}
			else
				_guiMacroExecutor.ExecuteViaExtenderProcess(macrotext, FWhenDone, isSync, processConEmu.Id, _startinfo.ConEmuConsoleExtenderExecutablePath);
		}

		/// <summary>
		/// Executes a ConEmu GUI Macro on the active console, see http://conemu.github.io/en/GuiMacro.html , synchronously.
		/// </summary>
		/// <param name="macrotext">The full macro command, see http://conemu.github.io/en/GuiMacro.html .</param>
		public GuiMacroResult ExecuteGuiMacroTextSync([NotNull] string macrotext)
		{
			if(macrotext == null)
				throw new ArgumentNullException(nameof(macrotext));

			Process processConEmu = _process;
			if(processConEmu == null)
				throw new InvalidOperationException("Cannot execute a macro because the console process is not running at the moment.");

			if(IsExecutingGuiMacrosInProcess)
				return _guiMacroExecutor.ExecuteInProcess(processConEmu.Id, macrotext);

			// Get result sync
			GuiMacroResult? retval = null;
			_guiMacroExecutor.ExecuteViaExtenderProcess(macrotext, result => retval = result, true, processConEmu.Id, _startinfo.ConEmuConsoleExtenderExecutablePath);
			if(retval == null)
				throw new InvalidOperationException("The GuiMacro has failed to execute and provide the result synchronously.");
			return retval.Value;
		}

		readonly GuiMacroExecutor _guiMacroExecutor;

		/// <summary>
		/// Non-NULL if we've requested ANSI log from ConEmu and are listening for it.
		/// </summary>
		[CanBeNull]
		private readonly AnsiLog _ansilog;

		/// <summary>
		/// Fires periodically while we're alive, allows to wire individual features.
		/// </summary>
		private EventHandler _evtPolling;

		/// <summary>
		/// Fires when we're done with, allows to wire individual features.
		/// </summary>
		private EventHandler _evtCleanupOnExit;

		/// <summary>
		/// Kills the whole console emulator process if it is running. This also terminates the console emulator window.	// TODO: kill payload process only when we know its pid
		/// </summary>
		public void KillConsoleEmulator()
		{
			try
			{
				if(!_process.HasExited)
					_process.Kill();
			}
			catch(Exception)
			{
				// Might be a race, so in between HasExited and Kill state could change, ignore possible errors here
			}
		}

		/// <summary>
		///     <para>Fires when the console process writes into its output or error stream. Gets a chunk of the raw ANSI stream contents.</para>
		///     <para>For processes which write immediately on startup, this event might fire some chunks before you sink it. To get notified reliably, use <see cref="ConEmuStartInfo.AnsiStreamChunkReceivedEventSink" />.</para>
		///     <para>To enable sinking this event, you must have <see cref="ConEmuStartInfo.IsReadingAnsiStream" /> set to <c>True</c> before starting the console process.</para>
		///     <para>If you're reading the ANSI log with <see cref="AnsiStreamChunkReceived" />, it's guaranteed that all the events for the log will be fired before <see cref="PayloadExited" />, and there will be no events afterwards.</para>
		/// </summary>
		[SuppressMessage("ReSharper", "DelegateSubtraction")]
		public event EventHandler<AnsiStreamChunkEventArgs> AnsiStreamChunkReceived
		{
			add
			{
				if(_ansilog == null)
					throw new InvalidOperationException("You cannot receive the ANSI stream data because the console process has not been set up to read the ANSI stream before running; set ConEmuStartInfo::IsReadingAnsiStream to True before starting the process.");
				_ansilog.AnsiStreamChunkReceived += value;
			}
			remove
			{
				if(_ansilog == null)
					throw new InvalidOperationException("You cannot receive the ANSI stream data because the console process has not been set up to read the ANSI stream before running; set ConEmuStartInfo::IsReadingAnsiStream to True before starting the process.");
				_ansilog.AnsiStreamChunkReceived -= value;
			}
		}

		/// <summary>
		///     <para>Fires when the console emulator process exits and stops rendering the terminal view. Note that the root command might have had stopped running long before this moment if not <see cref="WhenPayloadProcessExits.CloseTerminal" /> prevents terminating the terminal view immediately.</para>
		///     <para>For short-lived processes, this event might fire before you sink it. To get notified reliably, use <see cref="ConEmuStartInfo.ConsoleEmulatorExitedEventSink" />.</para>
		/// </summary>
		public event EventHandler ConsoleEmulatorExited;

		/// <summary>
		///     <para>Fires when the payload command exits within the terminal. If not <see cref="WhenPayloadProcessExits.CloseTerminal" />, the terminal stays, otherwise it closes also.</para>
		///     <para>For short-lived processes, this event might fire before you sink it. To get notified reliably, use <see cref="ConEmuStartInfo.PayloadExitedEventSink" />.</para>
		///     <para>If you're reading the ANSI log with <see cref="AnsiStreamChunkReceived" />, it's guaranteed that all the events for the log will be fired before <see cref="PayloadExited" />, and there will be no events afterwards.</para>
		/// </summary>
		public event EventHandler<ProcessExitedEventArgs> PayloadExited;

		public class HostContext
		{
			public HostContext([NotNull] void* hWndParent, bool isStatusbarVisibleInitial)
			{
				if(hWndParent == null)
					throw new ArgumentNullException(nameof(hWndParent));
				HWndParent = hWndParent;
				IsStatusbarVisibleInitial = isStatusbarVisibleInitial;
			}

			[NotNull]
			public readonly void* HWndParent;

			public readonly bool IsStatusbarVisibleInitial;
		}
	}
}