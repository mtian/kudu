﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Kudu.Contracts.Tracing;

namespace Kudu.Core.Infrastructure
{
    // http://blogs.msdn.com/b/bclteam/archive/2006/06/20/640259.aspx
    // http://stackoverflow.com/questions/394816/how-to-get-parent-process-in-net-in-managed-way
    public static class ProcessExtensions
    {
        internal static TimeSpan StandardOutputDrainTimeout = TimeSpan.FromSeconds(5);

        private const string GCDump32Exe = @"%ProgramFiles(x86)%\vsdiagagent\x86\GCDump32.exe";
        private const string GCDump64Exe = @"%ProgramFiles(x86)%\vsdiagagent\x64\GCDump64.exe";

        private readonly static Lazy<string> _clrRuntimeDirectory = new Lazy<string>(() =>
        {
            return RuntimeEnvironment.GetRuntimeDirectory();
        });

        private readonly static Lazy<string> _gcDumpExe = new Lazy<string>(() =>
        {
            string gcDumpExe = System.Environment.ExpandEnvironmentVariables(GCDump32Exe);
            if (ClrRuntimeDirectory.IndexOf(@"\Framework64\", StringComparison.OrdinalIgnoreCase) > 0)
            {
                gcDumpExe = System.Environment.ExpandEnvironmentVariables(GCDump64Exe);
            }

            return gcDumpExe;
        });

        private readonly static Lazy<bool> _supportGCDump = new Lazy<bool>(() =>
        {
            return File.Exists(_gcDumpExe.Value);
        });

        public static string ClrRuntimeDirectory
        {
            get { return _clrRuntimeDirectory.Value; }
        }

        public static bool SupportGCDump
        {
            get { return _supportGCDump.Value; }
        }

        public static void Kill(this Process process, bool includesChildren, ITracer tracer)
        {
            try
            {
                if (includesChildren)
                {
                    foreach (Process child in process.GetChildren(tracer))
                    {
                        SafeKillProcess(child, tracer);
                    }
                }
            }
            catch (Exception ex)
            {
                tracer.TraceError(ex);
            }
            finally
            {
                SafeKillProcess(process, tracer);
            }
        }              

        public static IEnumerable<Process> GetChildren(this Process process, ITracer tracer, bool recursive = true)
        {
            int pid = process.Id;
            Dictionary<int, List<int>> tree = GetProcessTree(tracer);
            return GetChildren(pid, tree, recursive).Select(cid => SafeGetProcessById(cid)).Where(p => p != null);
        }

        // recursively get children.
        // return depth-first (leaf child first).
        private static IEnumerable<int> GetChildren(int pid, Dictionary<int, List<int>> tree, bool recursive)
        {
            List<int> children;
            if (tree.TryGetValue(pid, out children))
            {
                List<int> result = new List<int>();
                foreach (int id in children)
                {
                    if (recursive)
                    {
                        result.AddRange(GetChildren(id, tree, recursive));
                    }
                    result.Add(id);
                }
                return result;
            }
            return Enumerable.Empty<int>();
        }

        /// <summary>
        /// Calculates the sum of TotalProcessorTime for the current process and all its children.
        /// </summary>
        public static TimeSpan GetTotalProcessorTime(this Process process, ITracer tracer)
        {
            try
            {
                var processes = process.GetChildren(tracer).Concat(new[] { process }).Select(p => new { Name = p.ProcessName, Id = p.Id, Cpu = p.TotalProcessorTime });
                var totalTime = TimeSpan.FromTicks(processes.Sum(p => p.Cpu.Ticks));
                var info = String.Join("+", processes.Select(p => String.Format("{0}({1},{2:0.000}s)", p.Name, p.Id, p.Cpu.TotalSeconds)).ToArray());
                tracer.Trace("Cpu: {0}=total({1:0.000}s)", info, totalTime.TotalSeconds);
                return totalTime;
            }
            catch (Exception ex)
            {
                tracer.TraceError(ex);
            }

            return process.TotalProcessorTime;
        }

        /// <summary>
        /// Get parent process.
        /// </summary>
        public static Process GetParentProcess(this Process process, ITracer tracer)
        {
            var pbi = new ProcessNativeMethods.ProcessInformation();
            try
            {
                int returnLength;
                int status = ProcessNativeMethods.NtQueryInformationProcess(process.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out returnLength);
                if (status != 0)
                {
                    throw new Win32Exception(status);
                }

                return Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32());
            }
            catch (Exception ex)
            {
                if (!process.ProcessName.Equals("w3wp", StringComparison.OrdinalIgnoreCase))
                {
                    tracer.Trace("GetParentProcess of {0}({1}) failed with {2}", process.ProcessName, process.Id, ex);
                }
                return null;
            }
        }

        /// <summary>
        /// Get parent id.
        /// </summary>
        public static int GetParentId(this Process process, ITracer tracer)
        {
            Process parent = process.GetParentProcess(tracer);
            return parent != null ? parent.Id : -1;
        }

        public static void MiniDump(this Process process, string dumpFile, MINIDUMP_TYPE dumpType)
        {
            using (var fs = new FileStream(dumpFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (!MiniDumpNativeMethods.MiniDumpWriteDump(process.Handle, (uint)process.Id, fs.SafeFileHandle, dumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        public static void GCDump(this Process process, string dumpFile, string resourcePath, int maxDumpCountK, ITracer tracer, TimeSpan idleTimeout)
        {
            var exe = new Executable(_gcDumpExe.Value, Path.GetDirectoryName(dumpFile), idleTimeout);
            exe.Execute(tracer, "\"{0}\" \"{1}\" \"{2}\" \"{3}\"", process.Id, dumpFile, resourcePath, maxDumpCountK);  
        }

        public static async Task<int> Start(this IProcess process, Stream output, Stream error, Stream input = null, IdleManager idleManager = null)
        {
            var cancellationTokenSource = new CancellationTokenSource();

            process.Start();

            var tasks = new List<Task>();

            if (input != null)
            {
                tasks.Add(CopyStreamAsync(input, process.StandardInput.BaseStream, idleManager, cancellationTokenSource.Token, closeAfterCopy: true));
            }

            tasks.Add(CopyStreamAsync(process.StandardOutput.BaseStream, output, idleManager, cancellationTokenSource.Token));
            tasks.Add(CopyStreamAsync(process.StandardError.BaseStream, error, idleManager, cancellationTokenSource.Token));

            idleManager.WaitForExit(process);

            // Process has exited, draining the stdout and stderr
            await FlushAllAsync(idleManager, cancellationTokenSource, tasks);

            return process.ExitCode;
        }

        private static async Task CopyStreamAsync(Stream from, Stream to, IdleManager idleManager, CancellationToken cancellationToken, bool closeAfterCopy = false)
        {
            try
            {
                byte[] bytes = new byte[1024];
                int read = 0;
                while ((read = await from.ReadAsync(bytes, 0, bytes.Length, cancellationToken)) != 0)
                {
                    idleManager.UpdateActivity();
                    await to.WriteAsync(bytes, 0, read, cancellationToken);
                }

                idleManager.UpdateActivity();
            }
            finally
            {
                // this is needed specifically for input stream
                // in order to tell executable that the input is done
                if (closeAfterCopy)
                {
                    to.Close();
                }
            }
        }

        private static async Task FlushAllAsync(IdleManager idleManager, CancellationTokenSource cancellationTokenSource, IEnumerable<Task> tasks)
        {
            var prevActivity = DateTime.MinValue;
            while (true)
            {
                // Wait for either delay or io tasks
                var delay = Task.Delay(StandardOutputDrainTimeout, cancellationTokenSource.Token);
                var stdio = Task.WhenAll(tasks);
                var completed = await Task.WhenAny(stdio, delay);

                // if delay commpleted first (meaning timeout), check if activity and continue to wait
                if (completed == delay)
                {
                    var lastActivity = idleManager.LastActivity;
                    if (lastActivity != prevActivity)
                    {
                        prevActivity = lastActivity;
                        continue;
                    }
                }

                // clean up all pending tasks by cancelling them
                // this is important so we don't have runnaway tasks
                cancellationTokenSource.Cancel();

                try
                {
                    // observe all tasks to avoid process crashes
                    await Task.WhenAll(stdio, delay);

                    // in case tasks completion races with cancellation
                    break;
                }
                catch (TaskCanceledException)
                {
                    // expected since we cancelled all task
                }

                // this means no activity within given time
                if (completed != stdio)
                {
                    throw new TimeoutException("Timeout draining standard input, output and error!");
                }

                // happy path
                break;
            }
        }

        private static Process SafeGetProcessById(int pid)
        {
            try
            {
                return Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // Process with an Id is not running.
                return null;
            }
        }

        private static void SafeKillProcess(Process process, ITracer tracer)
        {
            try
            {
                string processName = process.ProcessName;
                int pid = process.Id;
                process.Kill();
                tracer.Trace("Abort Process '{0}({1})'.", processName, pid);
            }
            catch (Exception)
            {
                if (!process.HasExited)
                {
                    throw;
                }
            }
        }

        private static Dictionary<int, List<int>> GetProcessTree(ITracer tracer)
        {
            var tree = new Dictionary<int, List<int>>();
            foreach (var proc in Process.GetProcesses())
            {
                Process parent = proc.GetParentProcess(tracer);
                if (parent != null)
                {
                    List<int> children = null;
                    if (!tree.TryGetValue(parent.Id, out children))
                    {
                        tree[parent.Id] = children = new List<int>();
                    }

                    children.Add(proc.Id);
                }
            }

            return tree;
        }

        [SuppressUnmanagedCodeSecurity]
        internal static class ProcessNativeMethods
        {
            [DllImport("ntdll.dll")]
            public static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref ProcessInformation processInformation, int processInformationLength, out int returnLength);

            [StructLayout(LayoutKind.Sequential)]
            public struct ProcessInformation
            {
                // These members must match PROCESS_BASIC_INFORMATION
                internal IntPtr Reserved1;
                internal IntPtr PebBaseAddress;
                internal IntPtr Reserved2_0;
                internal IntPtr Reserved2_1;
                internal IntPtr UniqueProcessId;
                internal IntPtr InheritedFromUniqueProcessId;
            }
        }
    }
}