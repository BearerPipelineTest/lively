﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace livelywpf.Core
{
    //Ref: 
    //https://github.com/rocksdanister/lively/discussions/342
    //https://wiki.videolan.org/documentation:modules/rc/
    public class VideoVlcPlayer : IWallpaper
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        public event EventHandler<WindowInitializedArgs> WindowInitialized;
        IntPtr HWND { get; set; }
        Process Proc { get; set; }
        LibraryModel Model { get; set; }
        LivelyScreen Display { get; set; }
        private readonly CancellationTokenSource ctsProcessWait = new CancellationTokenSource();
        private Task processWaitTask;
        private readonly int timeOut;
        //private readonly string ipcServerName;

        public VideoVlcPlayer(string path, LibraryModel model, LivelyScreen display, WallpaperScaler scaler = WallpaperScaler.fill)
        {
            //todo: hw accel, scaler, streaming flag..
            string cmdArgs =
                //hide menus and controls.
                "--qt-minimal-view " +
                //prevent player window resizing to video size.
                "--no-qt-video-autoresize " +
                //do not create system-tray icon.
                "--no-qt-system-tray " +
                //allow screensaver
                "--no-disable-screensaver " +
                //file path
                "\"" + path + "\"";

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "vlc", "vlc.exe"),
                UseShellExecute = false,
                WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "vlc"),
                Arguments = cmdArgs,
            };

            Process proc = new Process()
            {
                StartInfo = start,
            };

            this.Proc = proc;
            this.Model = model;
            this.Display = display;
            this.timeOut = 20000;
        }

        public async void Close()
        {
            TaskProcessWaitCancel();
            while (!IsProcessWaitDone())
            {
                await Task.Delay(1);
            }

            //Not reliable, app may refuse to close(open dialogue window.. etc)
            //Proc.CloseMainWindow();
            Terminate();
        }

        public IntPtr GetHWND()
        {
            return HWND;
        }

        public string GetLivelyPropertyCopyPath()
        {
            return null;
        }

        public Process GetProcess()
        {
            return Proc;
        }

        public LivelyScreen GetScreen()
        {
            return Display;
        }

        public LibraryModel GetWallpaperData()
        {
            return Model;
        }

        public WallpaperType GetWallpaperType()
        {
            return Model.LivelyInfo.Type;
        }

        public void Pause()
        {
            //todo
        }

        public void Play()
        {
            //todo
        }

        public void SendMessage(string msg)
        {
            //todo
        }

        public void SetHWND(IntPtr hwnd)
        {
            this.HWND = hwnd;
        }

        public void SetPlaybackPos(int pos)
        {
            //todo
        }

        public void SetScreen(LivelyScreen display)
        {
            this.Display = display;
        }

        public void SetVolume(int volume)
        {
            //todo
        }

        public async void Show()
        {
            if (Proc != null)
            {
                try
                {
                    Proc.Exited += Proc_Exited;
                    Proc.Start();
                    processWaitTask = Task.Run(() => HWND = WaitForProcesWindow().Result, ctsProcessWait.Token);
                    await processWaitTask;
                    if (HWND.Equals(IntPtr.Zero))
                    {
                        WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                        {
                            Success = false,
                            Error = new Exception(Properties.Resources.LivelyExceptionGeneral),
                            Msg = "Process window handle is zero."
                        });
                    }
                    else
                    {
                        WindowOperations.BorderlessWinStyle(HWND);
                        WindowOperations.RemoveWindowFromTaskbar(HWND);
                        //Program ready!
                        WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                        {
                            Success = true,
                            Error = null,
                            Msg = null
                        });
                        //todo: Restore livelyproperties.json settings here..
                    }
                }
                catch (OperationCanceledException e1)
                {
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                    {
                        Success = false,
                        Error = e1,
                        Msg = "Program wallpaper terminated early/user cancel."
                    });
                }
                catch (InvalidOperationException e2)
                {
                    //No GUI, program failed to enter idle state.
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                    {
                        Success = false,
                        Error = e2,
                        Msg = "Program wallpaper crashed/closed already!"
                    });
                }
                catch (Exception e3)
                {
                    WindowInitialized?.Invoke(this, new WindowInitializedArgs()
                    {
                        Success = false,
                        Error = e3,
                        Msg = ":("
                    });
                }
            }
        }

        private void Proc_Exited(object sender, EventArgs e)
        {
            Proc.Dispose();
            SetupDesktop.RefreshDesktop();
        }

        #region process task

        /// <summary>
        /// Function to search for window of spawned program.
        /// </summary>
        private async Task<IntPtr> WaitForProcesWindow()
        {
            if (Proc == null)
            {
                return IntPtr.Zero;
            }

            Proc.Refresh();
            //waiting for program messageloop to be ready (GUI is not guaranteed to be ready.)
            while (Proc.WaitForInputIdle(-1) != true)
            {
                ctsProcessWait.Token.ThrowIfCancellationRequested();
            }

            IntPtr wHWND = IntPtr.Zero;
            //Find process window.
            for (int i = 0; i < timeOut && Proc.HasExited == false; i++)
            {
                ctsProcessWait.Token.ThrowIfCancellationRequested();
                if (!IntPtr.Equals((wHWND = GetProcessWindow(Proc, true)), IntPtr.Zero))
                    break;
                await Task.Delay(1);
            }
            return wHWND;
        }

        /// <summary>
        /// Retrieve window handle of process.
        /// </summary>
        /// <param name="proc">Process to search for.</param>
        /// <param name="win32Search">Use win32 method to find window.</param>
        /// <returns></returns>
        private IntPtr GetProcessWindow(Process proc, bool win32Search = false)
        {
            if (Proc == null)
                return IntPtr.Zero;

            if (win32Search)
            {
                return FindWindowByProcessId(proc.Id);
            }
            else
            {
                proc.Refresh();
                //Issue(.net core) MainWindowHandle zero: https://github.com/dotnet/runtime/issues/32690
                return proc.MainWindowHandle;
            }
        }

        private IntPtr FindWindowByProcessId(int pid)
        {
            IntPtr HWND = IntPtr.Zero;
            NativeMethods.EnumWindows(new NativeMethods.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                _ = NativeMethods.GetWindowThreadProcessId(tophandle, out int cur_pid);
                if (cur_pid == pid)
                {
                    if (NativeMethods.IsWindowVisible(tophandle))
                    {
                        HWND = tophandle;
                        return false;
                    }
                }

                return true;
            }), IntPtr.Zero);

            return HWND;
        }

        /// <summary>
        /// Cancel waiting for pgm wp window to be ready.
        /// </summary>
        private void TaskProcessWaitCancel()
        {
            if (ctsProcessWait == null)
                return;

            ctsProcessWait.Cancel();
            ctsProcessWait.Dispose();
        }

        /// <summary>
        /// Check if started pgm ready(GUI window started).
        /// </summary>
        /// <returns>true: process ready/halted, false: process still starting.</returns>
        private bool IsProcessWaitDone()
        {
            var task = processWaitTask;
            if (task != null)
            {
                if ((task.IsCompleted == false
                || task.Status == TaskStatus.Running
                || task.Status == TaskStatus.WaitingToRun
                || task.Status == TaskStatus.WaitingForActivation))
                {
                    return false;
                }
                return true;
            }
            return true;
        }

        #endregion process task


        public void Stop()
        {
            throw new NotImplementedException();
        }

        public void Terminate()
        {
            try
            {
                Proc.Kill();
                Proc.Dispose();
            }
            catch { }
            SetupDesktop.RefreshDesktop();
        }
    }
}