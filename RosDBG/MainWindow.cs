﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Reflection;
using System.Diagnostics;
using WeifenLuo.WinFormsUI.Docking;
using AbstractPipe;
using DebugProtocol;
using KDBGProtocol;
using DbgHelpAPI;

namespace RosDBG
{
    public delegate void NoParamsDelegate();

    public partial class MainWindow : Form, IShell
    {
        //private RegisterView m_RegView = new RegisterView();
        private StatefulRegisterView m_RegView = new StatefulRegisterView();
        private BackTrace m_BackTrace = new BackTrace();
        private RawTraffic m_RawTraffic = new RawTraffic();
        private Locals m_Locals = new Locals();
        private MemoryWindow m_MemoryWindow = new MemoryWindow();
        private ProcThread m_ProcThread = new ProcThread();
        private Modules m_Modules = new Modules();
        private BreakpointWindow m_Breakpoints = new BreakpointWindow();

        private bool mRunning;
        private DebugConnection.Mode mConnectionMode;
        private ulong mCurrentEip;
        private string mSourceRoot = Settings.SourceDirectory, mCurrentFile;
        private int mCurrentLine;
        private DebugConnection mConnection = new DebugConnection();
        private SymbolContext mSymbolContext;
        Dictionary<uint, Module> mModules = new Dictionary<uint, Module>();
        private Dictionary<string, SourceView> mSourceFiles = new Dictionary<string, SourceView>();

        //public event CopyEventHandler CopyEvent;

        public MainWindow()
        {
            InitializeComponent();

            // Setup the logger
            RosDiagnostics.SetupLogger();

            RosDiagnostics.DebugTrace(RosDiagnostics.TraceType.Info, "Initialising application");

            mSymbolContext = new SymbolContext();

            RegisterControl(m_RegView);
            RegisterControl(m_BackTrace);
            RegisterControl(m_RawTraffic);
            RegisterControl(m_Locals);
            RegisterControl(m_MemoryWindow);
            RegisterControl(m_ProcThread);
            RegisterControl(m_Modules);
            RegisterControl(m_Breakpoints);

            m_Locals.Show(dockPanel, DockState.DockRight);
            m_RegView.Show(dockPanel, DockState.DockRight);
            m_Breakpoints.Show(dockPanel, DockState.DockRight);
            m_RegView.Activate();
            m_BackTrace.Show(dockPanel, DockState.DockBottom);
            m_RawTraffic.Show(dockPanel);
            m_Modules.Show(dockPanel);
            m_ProcThread.Show(dockPanel);
            ReactOSWeb web = new ReactOSWeb();
            web.Show(dockPanel);
        }

        void ComposeTitleString()
        {
            FocusAddress(mCurrentEip);

            string mode;
            switch (mConnectionMode)
            {
                case DebugConnection.Mode.ClosedMode: mode = "Closed"; break;
                case DebugConnection.Mode.PipeMode:   mode = "Pipe";   break;
                case DebugConnection.Mode.SerialMode: mode = "Serial"; break;
                case DebugConnection.Mode.SocketMode: mode = "Socket"; break;
                default: mode = "Unknown"; break;
            }

            toolStripStatusConnectionMode.Text = mode;
            if (mConnectionMode == DebugConnection.Mode.ClosedMode)
            {
                toolStripStatusConnected.ForeColor = Color.Crimson;
                toolStripStatusConnected.Text = "Not connected";
            }
            else
            {
                if (mRunning)
                {
                    toolStripStatusConnected.ForeColor = Color.Green;
                    toolStripStatusConnected.Text = "Debug";
                }
                else
                {
                    toolStripStatusConnected.ForeColor = Color.Yellow;
                    toolStripStatusConnected.Text = "Waiting";
                }
            }

            if (mCurrentFile.CompareTo("unknown") != 0)
            {
                toolStripStatusSourceLocation.Visible = true;
                toolStripStatusSourceLocationFile.Text = mCurrentFile;
                toolStripStatusSourceLocationLine.Text = mCurrentLine.ToString();
                toolStripStatusSourceLocationColon.Visible = true;
            }
            else
            {
                toolStripStatusSourceLocation.Visible = false;
                toolStripStatusSourceLocationFile.Text = string.Empty;
                toolStripStatusSourceLocationLine.Text = string.Empty;
                toolStripStatusSourceLocationColon.Visible = false;
            }
        }

        void DebugModuleChangedEvent(object sender, DebugModuleChangedEventArgs args)
        {
            Module themod;
            if (!mModules.TryGetValue(args.ModuleAddr, out themod) ||
                themod.ShortName != args.ModuleName.ToLower())
            {
                mModules[args.ModuleAddr] = new Module(args.ModuleAddr, args.ModuleName);
                mSymbolContext.LoadModule(args.ModuleName, args.ModuleAddr);
            }
            Invoke(Delegate.CreateDelegate(typeof(NoParamsDelegate), this, "ComposeTitleString"));
        }

        void DebugRunningChangeEvent(object sender, DebugRunningChangeEventArgs args)
        {
            mRunning = args.Running;
            Invoke(Delegate.CreateDelegate(typeof(NoParamsDelegate), this, "ComposeTitleString"));
            Invoke(Delegate.CreateDelegate(typeof(NoParamsDelegate), this, "UpdateDebuggerMenu"));
        }

        void DebugConnectionModeChangedEvent(object sender, DebugConnectionModeChangedEventArgs args)
        {
            mConnectionMode = args.Mode;
            Invoke(Delegate.CreateDelegate(typeof(NoParamsDelegate), this, "ComposeTitleString"));
        }

        void DebugRegisterChangeEvent(object sender, DebugRegisterChangeEventArgs args)
        {
            mCurrentEip = args.Registers.Eip;
            Invoke(Delegate.CreateDelegate(typeof(NoParamsDelegate), this, "ComposeTitleString"));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            mSymbolContext.Initialize();
            mConnection.DebugConnectionModeChangedEvent += DebugConnectionModeChangedEvent;
            mConnection.DebugRunningChangeEvent += DebugRunningChangeEvent;
            mConnection.DebugRegisterChangeEvent += DebugRegisterChangeEvent;
            mConnection.DebugModuleChangedEvent += DebugModuleChangedEvent;
            ComposeTitleString();
            mSymbolContext.ReactosOutputPath = Settings.OutputDirectory;
        }

        public void RegisterControl(Control ctrl)
        {
            IUseDebugConnection usedbg = ctrl as IUseDebugConnection;
            if (usedbg != null)
                usedbg.SetDebugConnection(mConnection);
            IUseSymbols usesym = ctrl as IUseSymbols;
            if (usesym != null)
                usesym.SetSymbolProvider(mSymbolContext);
            IUseShell useshell = ctrl as IUseShell;
            if (useshell != null)
                useshell.SetShell(this);
        }

        private void OpenFile(object sender, EventArgs e)
        {
            OpenFileDialog fileDialog = new OpenFileDialog();
            fileDialog.Filter = "Sourcefiles (*.c;*.cpp)|*.c;*.cpp";
            if (fileDialog.ShowDialog() == DialogResult.OK)
            {
                OpenSourceFile(fileDialog.FileName);
            }
        }

        private void ExitToolsStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void ToolBarToolStripMenuItem_Click(object sender, EventArgs e)
        {
            toolStrip.Visible = toolBarToolStripMenuItem.Checked;
        }

        private void StatusBarToolStripMenuItem_Click(object sender, EventArgs e)
        {
            statusStrip.Visible = statusBarToolStripMenuItem.Checked;
        }

        private void MainWindowMDI_FormClosing(object sender, FormClosingEventArgs e)
        {
            RosDiagnostics.DebugTrace(RosDiagnostics.TraceType.Info, "Closing application");
            mConnection.Close(true);
            SaveWindowSettings();
        }

        void UpdateDebuggerMenu()
        {
            if (mConnection.ConnectionMode == DebugConnection.Mode.ClosedMode)
            {
                continueToolStripButton.Enabled = continueToolStripMenuItem.Enabled = false;
                breakToolStripButton.Enabled = breakToolStripMenuItem.Enabled = false;
                nextToolStripButton.Enabled = nextToolStripMenuItem.Enabled = false;
                stepToolStripButton.Enabled = stepToolStripMenuItem.Enabled = false;
            }
            else
            {
                continueToolStripButton.Enabled = continueToolStripMenuItem.Enabled = !mRunning;
                breakToolStripButton.Enabled = breakToolStripMenuItem.Enabled = mRunning;
                nextToolStripButton.Enabled = nextToolStripMenuItem.Enabled = !mRunning;
                stepToolStripButton.Enabled = stepToolStripMenuItem.Enabled = !mRunning;
            }
        }

        public void FocusAddress(ulong eipToFocus)
        {
            KeyValuePair<string, int> fileline = mSymbolContext.GetFileAndLine(eipToFocus);
            mCurrentFile = fileline.Key;
            mCurrentLine = fileline.Value;
            TryToDisplaySource();
        }

        void Rehighlight(SourceView vw)
        {
            vw.ClearHighlight();
            vw.AddHighlight(mCurrentLine, Color.SteelBlue, Color.White);
            vw.ScrollTo(mCurrentLine);
        }

        void TryToDisplaySource()
        {
            if (mCurrentFile == null || mCurrentFile == "unknown") return;
            OpenSourceFile(Path.Combine(mSourceRoot, mCurrentFile));
        }

        private void OpenSourceFile(string FileName)
        {
            SourceView theSourceView;
            if (File.Exists(FileName))
            {
                if (mSourceFiles.TryGetValue(FileName, out theSourceView))
                    Rehighlight(theSourceView);
                else
                {
                    theSourceView = new SourceView(Path.GetFileName(FileName));
                    mSourceFiles[FileName] = theSourceView;
                    theSourceView.SourceFile = FileName;
                    Rehighlight(theSourceView);
                    theSourceView.Show(dockPanel);  
                }
            }
        }

        public DebugConnection DebugConnection
        {
            get { return mConnection; }
        }

        private void breakToolStripMenuItem_Click(object sender, EventArgs e)
        {
            mConnection.Break();
        }

        private void continueToolStripMenuItem_Click(object sender, EventArgs e)
        {
            mConnection.Go();
        }

        private void stepToolStripMenuItem_Click(object sender, EventArgs e)
        {
            mConnection.Step();
        }

        private void nextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            mConnection.Next();
        }

        private void contentsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ReactOSWeb Help = new ReactOSWeb("Help", "http://www.reactos.org/wiki/index.php/ReactOS_Remote_Debugger");
            Help.Show(dockPanel);  
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Settings.ShowSettings();
            mSourceRoot = Settings.SourceDirectory;
            mSymbolContext.ReactosOutputPath = Settings.OutputDirectory;
        }

        private void consoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_RawTraffic.Show(dockPanel);
        }

        private void connectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mConnection.ConnectionMode == DebugConnection.Mode.ClosedMode)
            {
                Connect newConnection = new Connect();
                if (newConnection.ShowDialog() == DialogResult.OK)
                {
                    mConnection.Close();
                    switch (newConnection.Type)
                    {
                        case Connect.ConnectionType.Serial:
                            mConnection.StartSerial(newConnection.ComPort, newConnection.Baudrate);
                            break;
                        case Connect.ConnectionType.Pipe:
                            mConnection.StartPipe(newConnection.PipeName, newConnection.PipeMode);
                            break;
                        case Connect.ConnectionType.Socket:
                            mConnection.StartTCP(newConnection.Host, newConnection.Port);
                            break;
                    }
                    if (mConnection.ConnectionMode != DebugConnection.Mode.ClosedMode)
                    {
                        connectToolStripMenuItem.Text = "&Disconnect";
                    }
                }
            }
            else
            {
                mConnection.Close();
                UpdateDebuggerMenu();
                connectToolStripMenuItem.Text = "&Connect";
            }
        }

        private void memoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_MemoryWindow.Show(dockPanel);
        }

        private void webbrowserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ReactOSWeb web = new ReactOSWeb();
            web.Show(dockPanel); 
        }

        private void registerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_RegView.Show(dockPanel, DockState.DockRight);
        }

        private void localsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_Locals.Show(dockPanel, DockState.DockRight);
        }

        private void procThreadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_ProcThread.Show(dockPanel);
        }

        private void modulesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_Modules.Show(dockPanel);
        }

        private void backtraceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_BackTrace.Show(dockPanel, DockState.DockBottom);
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ((ToolWindow)dockPanel.ActiveDocument.DockHandler.Form).Save(
                ((ToolWindow)dockPanel.ActiveDocument.DockHandler.Form).GetDocumentName());
        }

        private void SaveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ((ToolWindow)dockPanel.ActiveDocument.DockHandler.Form).SaveAs(
                ((ToolWindow)dockPanel.ActiveDocument.DockHandler.Form).GetDocumentName());
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AboutDlg about = new AboutDlg();
            about.ShowDialog(this); 
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            dockPanel.ActiveDocumentChanged -= dockPanel_ActiveDocumentChanged;
        }

        private void dockPanel_ActiveDocumentChanged(object sender, EventArgs e)
        {
            try
            {
                ToolWindow Wnd = (ToolWindow)dockPanel.ActiveDocument.DockHandler.Form;

                saveToolStripButton.Enabled = Wnd.IsCmdEnabled(ToolWindow.Commands.Save);
                saveToolStripMenuItem.Enabled = Wnd.IsCmdEnabled(ToolWindow.Commands.Save);
                saveAsToolStripMenuItem.Enabled = Wnd.IsCmdEnabled(ToolWindow.Commands.SaveAs);
                printToolStripButton.Enabled = Wnd.IsCmdEnabled(ToolWindow.Commands.Print);
                printToolStripMenuItem.Enabled = Wnd.IsCmdEnabled(ToolWindow.Commands.Print);
            }
            catch (NullReferenceException ex)
            {
                RosDiagnostics.DebugTrace(RosDiagnostics.TraceType.Exception, "Null reference : " + ex.Message);
            }
            catch (Exception)
            {
                RosDiagnostics.DebugTrace(RosDiagnostics.TraceType.Exception, "Unexpected error");
            }
        }

        private void printToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ((ToolWindow)dockPanel.ActiveDocument.DockHandler.Form).Print(true);
        }

        private void printToolStripButton_Click(object sender, EventArgs e)
        {
            ((ToolWindow)dockPanel.ActiveDocument.DockHandler.Form).Print(false);
        }

        private void stepToolStripButton_Click(object sender, EventArgs e)
        {
            stepToolStripMenuItem_Click(sender, e);
        }

        private void nextToolStripButton_Click(object sender, EventArgs e)
        {
            nextToolStripMenuItem_Click(sender, e);
        }

        private void externalToolsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExtTools exTools = new ExtTools();
            if (exTools.ShowDialog(this) == DialogResult.OK)
            {
                Settings.Save();
                UpdateExternalToolsMenu();
            }
        }

        private void UpdateExternalToolsMenu()
        {
            int i = 0;
            bool bFirst = true;
            while (true)
            {
                if ((toolsMenu.DropDownItems[i].Tag != null) && 
                    (toolsMenu.DropDownItems[i].Tag.ToString() == "tool"))
                    toolsMenu.DropDownItems.Remove(toolsMenu.DropDownItems[i]);
                else
                    i++;
                if (i >= toolsMenu.DropDownItems.Count - 1)
                    break;
            }
            if (Settings.ExternalTools != null)
            {
                foreach (object o in Settings.ExternalTools)
                {
                    ToolStripMenuItem item = new ToolStripMenuItem(o.ToString(), null,
                        new System.EventHandler(this.LaunchExternalToolToolStripMenuItem_Click),
                        ((ExternalTool)o).Path);
                    item.Tag = "tool";
                    toolsMenu.DropDownItems.Insert(bFirst ? 0 : 1, item);
                    bFirst = false;
                }
            }
        }

        private void MainWindow_Load(object sender, EventArgs e)
        {
            UpdateExternalToolsMenu();
        }

        private void LaunchExternalToolToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(((ToolStripMenuItem)sender).Name);
            }
            catch (Exception)
            {

            }
        }
        private void SaveWindowSettings()
        {
            RosDBG.Properties.Settings.Default.Size = this.Size;
            RosDBG.Properties.Settings.Default.Location = this.Location;
            RosDBG.Properties.Settings.Default.WindowState = this.WindowState;
            RosDBG.Properties.Settings.Default.Save();

        }

    }

}
