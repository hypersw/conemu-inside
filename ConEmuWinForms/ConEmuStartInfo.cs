using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using JetBrains.Annotations;

namespace ConEmu.WinForms
{
	/// <summary>
	/// Startup configuration for the Console Emulator run within the control.
	/// </summary>
	public sealed class ConEmuStartInfo
	{
		private EventHandler<AnsiStreamChunkEventArgs> _ansiStreamChunkReceivedEventSink;

		private EventHandler _consoleEmulatorExitedEventSink;

		readonly IDictionary<string, string> _environment = new Dictionary<string, string>();

		private bool _isElevated;

		private bool _isReadingAnsiStream;

		private bool _isUsedUp;

		private EventHandler<ProcessExitedEventArgs> _payloadExitedEventSink;

		[NotNull]
		private string _sConEmuConsoleExtenderExecutablePath = "";

		[NotNull]
		private string _sConEmuConsoleServerExecutablePath = "";

		[NotNull]
		private string _sConEmuExecutablePath = "";

		private string _sConsoleCommandLine = ConEmuConstants.DefaultConsoleCommandLine;

		private string _sStartupDirectory;

		private WhenPayloadProcessExits _whenPayloadProcessExits = WhenPayloadProcessExits.KeepTerminalAndShowMessage;

		public ConEmuStartInfo()
		{
			ConEmuExecutablePath = InitConEmuLocation();
		}

		/// <summary>
		///     <para>Gets or sets an event sink for <see cref="ConEmuSession.AnsiStreamChunkReceived" /> to get reliably notified even for data written by the console process on its very startup.</para>
		///     <para>Settings this to a non-<c>NULL</c> value also implies on </para>
		/// </summary>
		[CanBeNull]
		public EventHandler<AnsiStreamChunkEventArgs> AnsiStreamChunkReceivedEventSink
		{
			get
			{
				return _ansiStreamChunkReceivedEventSink;
			}
			set
			{
				AssertNotUsedUp();
				_ansiStreamChunkReceivedEventSink = value;
			}
		}

		/// <summary>
		/// Gets or sets the path to the ConEmu console extender (<c>ConEmuC.exe</c>).
		/// Will be autodetected from the path to this DLL or from <see cref="ConEmuExecutablePath" /> if possible.
		/// </summary>
		[NotNull]
		public string ConEmuConsoleExtenderExecutablePath
		{
			get
			{
				return _sConEmuConsoleExtenderExecutablePath;
			}
			set
			{
				if(value == null)
					throw new ArgumentNullException(nameof(value));
				if((value == "") && (_sConEmuConsoleExtenderExecutablePath == ""))
					return;
				if(value == "")
					throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot reset path to an empty string.");
				_sConEmuConsoleExtenderExecutablePath = value; // Delay existence check 'til we call it
			}
		}

		/// <summary>
		/// Gets or sets the path to the ConEmu console server (<c>ConEmuCD.dll</c>). MUST match the processor architecture of the current process.
		/// Will be autodetected from the path to this DLL or from <see cref="ConEmuExecutablePath" /> if possible.
		/// </summary>
		[NotNull]
		public string ConEmuConsoleServerExecutablePath
		{
			get
			{
				return _sConEmuConsoleServerExecutablePath;
			}
			set
			{
				if(value == null)
					throw new ArgumentNullException(nameof(value));
				if((value == "") && (_sConEmuConsoleServerExecutablePath == ""))
					return;
				if(value == "")
					throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot reset path to an empty string.");
				_sConEmuConsoleServerExecutablePath = value; // Delay existence check 'til we call it
			}
		}

		/// <summary>
		/// Gets or sets the path to the <c>ConEmu.exe</c> which will be the console emulator root process.
		/// Will be autodetected from the path to this DLL if possible.
		/// </summary>
		[NotNull]
		public string ConEmuExecutablePath
		{
			get
			{
				return _sConEmuExecutablePath;
			}
			set
			{
				if(value == null)
					throw new ArgumentNullException(nameof(value));
				if((value == "") && (_sConEmuExecutablePath == ""))
					return;
				if(value == "")
					throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot reset path to an empty string.");
				_sConEmuExecutablePath = value; // Delay existence check 'til we call it

				if(_sConEmuConsoleExtenderExecutablePath == "")
					_sConEmuConsoleExtenderExecutablePath = TryDeriveConEmuConsoleExtenderExecutablePath(_sConEmuExecutablePath);
				if(_sConEmuConsoleServerExecutablePath == "")
					_sConEmuConsoleServerExecutablePath = TryDeriveConEmuConsoleServerExecutablePath(_sConEmuExecutablePath);
			}
		}

		/// <summary>
		///     <para>The command line to execute in the console emulator as the top-level process. The session terminates when this command exits.</para>
		///     <para>The default is <see cref="ConEmuConstants.DefaultConsoleCommandLine" />.</para>
		///     <para>This property cannot be changed when the process is running.</para>
		/// </summary>
		[NotNull]
		public string ConsoleCommandLine
		{
			get
			{
				return _sConsoleCommandLine;
			}
			set
			{
				AssertNotUsedUp();
				_sConsoleCommandLine = value;
			}
		}

		/// <summary>
		///     <para>Gets or sets an event sink for <see cref="ConEmuSession.ConsoleEmulatorExited" /> to get reliably notified even for short-lived processes.</para>
		/// </summary>
		[CanBeNull]
		public EventHandler ConsoleEmulatorExitedEventSink
		{
			get
			{
				return _consoleEmulatorExitedEventSink;
			}
			set
			{
				AssertNotUsedUp();
				_consoleEmulatorExitedEventSink = value;
			}
		}

		/// <summary>
		///     <para>Gets or sets whether the console process is to be run elevated (an elevation prompt will be shown).</para>
		///     <para>The default is <c>False</c>.</para>
		///     <para>This property cannot be changed when the process is running.</para>
		/// </summary>
		public bool IsElevated
		{
			get
			{
				return _isElevated;
			}
			set
			{
				AssertNotUsedUp();
				_isElevated = value;
			}
		}

		/// <summary>
		///     <para>Gets or sets whether the console emulator will be reading the raw ANSI stream of the console and firing the <see cref="ConEmuSession.AnsiStreamChunkReceived" /> events (and notifying <see cref="AnsiStreamChunkReceivedEventSink" />).</para>
		///     <para>This can only be decided on before the console process starts.</para>
		///     <para>Setting <see cref="AnsiStreamChunkReceivedEventSink" /> to a non-<c>NULL</c> value implies on a <c>True</c> value for this property.</para>
		/// </summary>
		public bool IsReadingAnsiStream
		{
			get
			{
				return _isReadingAnsiStream || (_ansiStreamChunkReceivedEventSink != null);
			}
			set
			{
				AssertNotUsedUp();
				if((!value) && (_ansiStreamChunkReceivedEventSink != null))
					throw new ArgumentOutOfRangeException(nameof(value), false, "Cannot turn IsReadingAnsiStream off when AnsiStreamChunkReceivedEventSink has a non-NULL value because it implies on a True value for this property.");
				_isReadingAnsiStream = value;
			}
		}

		/// <summary>
		///     <para>Gets or sets an event sink for <see cref="ConEmuSession.PayloadExited" /> to get reliably notified even for short-lived processes.</para>
		/// </summary>
		[CanBeNull]
		public EventHandler<ProcessExitedEventArgs> PayloadExitedEventSink
		{
			get
			{
				return _payloadExitedEventSink;
			}
			set
			{
				AssertNotUsedUp();
				_payloadExitedEventSink = value;
			}
		}

		/// <summary>
		/// Optional. Overrides the startup directory for the console process.
		/// </summary>
		[CanBeNull]
		public string StartupDirectory
		{
			get
			{
				return _sStartupDirectory;
			}
			set
			{
				AssertNotUsedUp();
				_sStartupDirectory = value;
			}
		}

		/// <summary>
		///     <para>Gets or sets whether the terminal emulator view should keep displaying the last contents after the console process specified in <see cref="ConsoleCommandLine" /> exits.</para>
		///     <para>See comments on enum members for details on specific behavior.</para>
		///     <para>The default is <see cref="WinForms.WhenPayloadProcessExits.KeepTerminalAndShowMessage" />.</para>
		///     <para>This property cannot be changed when the process is running.</para>
		/// </summary>
		public WhenPayloadProcessExits WhenPayloadProcessExits
		{
			get
			{
				return _whenPayloadProcessExits;
			}
			set
			{
				AssertNotUsedUp();
				_whenPayloadProcessExits = value;
			}
		}

		/// <summary>
		/// Gets the startup environment variables. This does not reflect the env vars of a running console process.
		/// </summary>
		[NotNull]
		public IEnumerable<string> EnumEnv()
		{
			return _environment.Keys.ToArray();
		}

		/// <summary>
		/// Gets the startup environment variables. This does not reflect the env vars of a running console process.
		/// </summary>
		/// <param name="name"></param>
		/// <returns></returns>
		[CanBeNull]
		public string GetEnv([NotNull] string name)
		{
			if(string.IsNullOrEmpty(name))
				throw new ArgumentNullException(nameof(name));
			string value;
			_environment.TryGetValue(name, out value);
			return value;
		}

		/// <summary>
		/// Sets the startup environment variables for the console process, before it is started.
		/// </summary>
		/// <param name="name"></param>
		/// <param name="value"></param>
		public void SetEnv([NotNull] string name, [CanBeNull] string value)
		{
			if(string.IsNullOrEmpty(name))
				throw new ArgumentNullException(nameof(name));
			AssertNotUsedUp();
			if(value == null)
				_environment.Remove(name);
			else
				_environment[name] = value;
		}

		private void AssertNotUsedUp()
		{
			if(_isUsedUp)
				throw new InvalidOperationException("This change is not possible because the start info object has already been used up.");
		}

		[NotNull]
		private static string InitConEmuLocation()
		{
			// Look alongside this DLL and in a subfolder
			string asmpath = new Uri(Assembly.GetExecutingAssembly().EscapedCodeBase, UriKind.Absolute).LocalPath;
			if(string.IsNullOrEmpty(asmpath))
				return "";
			string dir = Path.GetDirectoryName(asmpath);
			if(string.IsNullOrEmpty(dir))
				return "";

			// ConEmu.exe in the same folder as this control DLL
			string candidate = Path.Combine(dir, ConEmuConstants.ConEmuExeName);
			if(File.Exists(candidate))
				return candidate;

			// ConEmu.exe in a subfolder (it's convenient to have all managed product DLLs in the same folder for assembly resolve, and it might be handy to put multifile native deps like ConEmu in a subfolder)
			candidate = Path.Combine(Path.Combine(dir, ConEmuConstants.ConEmuSubfolderName), ConEmuConstants.ConEmuExeName);
			if(File.Exists(candidate))
				return candidate;

			// Not found by our standard means, rely on user to set path, otherwise will fail to start
			return "";
		}

		/// <summary>
		/// Marks this instance as used-up and prevens further modifications.
		/// </summary>
		internal void MarkAsUsedUp()
		{
			_isUsedUp = true;
		}

		[NotNull]
		private static string TryDeriveConEmuConsoleExtenderExecutablePath([NotNull] string sConEmuPath)
		{
			if(sConEmuPath == null)
				throw new ArgumentNullException(nameof(sConEmuPath));
			if(sConEmuPath == "")
				return "";
			string dir = Path.GetDirectoryName(sConEmuPath);
			if(string.IsNullOrEmpty(dir))
				return "";

			string candidate = Path.Combine(dir, ConEmuConstants.ConEmuConsoleExtenderExeName);
			if(File.Exists(candidate))
				return candidate;

			candidate = Path.Combine(Path.Combine(dir, ConEmuConstants.ConEmuSubfolderName), ConEmuConstants.ConEmuConsoleExtenderExeName);
			if(File.Exists(candidate))
				return candidate;

			return "";
		}

		[NotNull]
		private static string TryDeriveConEmuConsoleServerExecutablePath([NotNull] string sConEmuPath)
		{
			if(sConEmuPath == null)
				throw new ArgumentNullException(nameof(sConEmuPath));
			if(sConEmuPath == "")
				return "";
			string dir = Path.GetDirectoryName(sConEmuPath);
			if(string.IsNullOrEmpty(dir))
				return "";

			// Make up the file name for the CPU arch of the current process, as we're gonna load it in-process
			string sFileName = ConEmuConstants.ConEmuConsoleServerFileNameNoExt;
			if(IntPtr.Size == 8)
				sFileName += "64";
			sFileName += ".dll";

			string candidate = Path.Combine(dir, sFileName);
			if(File.Exists(candidate))
				return candidate;

			candidate = Path.Combine(Path.Combine(dir, ConEmuConstants.ConEmuSubfolderName), sFileName);
			if(File.Exists(candidate))
				return candidate;

			return "";
		}
	}
}