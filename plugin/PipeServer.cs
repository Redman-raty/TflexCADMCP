using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Control = System.Windows.Forms.Control;


namespace TFlexMcp
{
    internal sealed class PipeServer : IDisposable
    {
        private const int RequestLimit = 1024 * 1024;
        private const int ResponseLimit = 64 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly Func<Dictionary<string, object>, object> execute;
        private readonly Control dispatcher;
        private volatile bool stopping;
        private readonly NamedPipeServerStream pipe;

        public PipeServer(string name, Func<Dictionary<string, object>, object> execute)
        {
            this.execute = execute;
            dispatcher = new Control();
            try
            {
                // Create the handle on the CAD thread before starting the listener.
                if (dispatcher.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the CAD UI dispatcher.");
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Deny));
                using (var identity = WindowsIdentity.GetCurrent())
                    security.AddAccessRule(new PipeAccessRule(
                        identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 65536, 65536, security);
            }
            catch
            {
                dispatcher.Dispose();
                throw;
            }
            ObserveFailure(Task.Run((Func<Task>)Serve));
        }

        public void Dispose()
        {
            stopping = true;
            pipe.Dispose();
            dispatcher.Dispose();
        }

        private async Task Serve()
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = ResponseLimit, RecursionLimit = 40 };
            while (!stopping)
            {
                bool accepted = false;
                try
                {
                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    accepted = true;
                    object response;
                    bool dispatching = false;
                    try
                    {
                        var length = BitConverter.ToInt32(await ReadFramePart(4).ConfigureAwait(false), 0);
                        if (length <= 0 || length > RequestLimit)
                            throw new CadError("invalid_request", "Invalid request frame length.");
                        var request = serializer.DeserializeObject(
                            Utf8.GetString(await ReadFramePart(length).ConfigureAwait(false))) as Dictionary<string, object>;
                        if (request == null)
                            throw new CadError("invalid_request", "Expected a JSON object.");
                        dispatching = true;
                        response = new { ok = true, result = await OnUiThread(request).ConfigureAwait(false) };
                    }
                    catch (CadError ex)
                    {
                        response = new { ok = false, error = new { code = ex.Code, message = ex.Message, details = ex.Details } };
                    }
                    catch (Exception ex)
                    {
                        if (stopping || (!dispatching && (ex is IOException || ex is TimeoutException)))
                            throw;
                        response = new { ok = false, error = new
                            { code = dispatching ? "cad_error" : "invalid_request", message = ex.Message, type = ex.GetType().Name } };
                    }

                    byte[] payload;
                    try
                    {
                        payload = Utf8.GetBytes(serializer.Serialize(response));
                        if (payload.Length > ResponseLimit)
                            throw new InvalidOperationException();
                    }
                    catch (InvalidOperationException)
                    {
                        payload = Utf8.GetBytes("{\"ok\":false,\"error\":{\"code\":\"response_too_large\",\"message\":\"Cannot serialize the CAD response within the frame limit.\"}}");
                    }
                    await WithTimeout(pipe.WriteAsync(BitConverter.GetBytes(payload.Length), 0, 4)).ConfigureAwait(false);
                    await WithTimeout(pipe.WriteAsync(payload, 0, payload.Length)).ConfigureAwait(false);
                    // DisconnectNamedPipe can discard unread bytes, so wait for an acknowledgement.
                    await ReadFramePart(1).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (stopping) return;
                    if (!(ex is IOException) && !(ex is TimeoutException))
                        throw;
                }
                finally
                {
                    if (accepted && !stopping)
                    {
                        try { pipe.Disconnect(); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { if (!stopping) throw; }
                    }
                }
            }
        }

        private async Task<byte[]> ReadFramePart(int size)
        {
            var data = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                var read = pipe.ReadAsync(data, offset, size - offset);
                await WithTimeout(read).ConfigureAwait(false);
                int count = await read.ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            return data;
        }

        private static async Task WithTimeout(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != task)
            {
                // Disconnect in Serve releases pending pipe IO. Observe any later IO error.
                ObserveFailure(task);
                throw new TimeoutException("Named-pipe I/O timed out.");
            }
            await task.ConfigureAwait(false);
        }

        private static void ObserveFailure(Task task)
        {
            task.ContinueWith(fault => { GC.KeepAlive(fault.Exception); }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private Task<object> OnUiThread(Dictionary<string, object> request)
        {
            var result = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (stopping) throw new ObjectDisposedException("PipeServer");
                    result.SetResult(execute(request));
                }
                catch (Exception ex) { result.SetException(ex); }
            }));
            return result.Task;
        }
    }

    internal sealed class CadError : Exception
    {
        public string Code { get; private set; }
        public object Details { get; private set; }

        public CadError(string code, string message, object details = null) : base(message)
        {
            Code = code;
            Details = details;
        }
    }
}
