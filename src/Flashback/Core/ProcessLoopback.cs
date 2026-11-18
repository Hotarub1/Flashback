using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Flashback.Core;

/// <summary>
/// Opens a capture of what one program (and the programs it started) is playing, using Windows' process loopback
/// (Windows 10 2004 and later). This is how each app gets its own audio track.
/// </summary>
public static class ProcessLoopback
{
    private const string VirtualDevice = @"VAD\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0;
    private const ushort VtBlob = 65;
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>
    /// Returns an audio client that captures <paramref name="processId"/>'s sound. It has no device format: initialize
    /// it in shared mode with loopback flags and an explicit PCM format.
    /// </summary>
    public static AudioClient Activate(int processId, int timeoutMs = 5000)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type; { targetProcessId; mode } } inside a VT_BLOB PROPVARIANT.
        IntPtr parameters = Marshal.AllocHGlobal(12);
        IntPtr variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(parameters, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(parameters, 4, processId);
            Marshal.WriteInt32(parameters, 8, IncludeTargetProcessTree);
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(variant, i, 0);
            Marshal.WriteInt16(variant, 0, unchecked((short)VtBlob));
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 16, parameters);

            var handler = new CompletionHandler();
            int hr = ActivateAudioInterfaceAsync(VirtualDevice, AudioClientIid, variant, handler, out IActivateAudioInterfaceAsyncOperation op);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                if (!handler.Done.Wait(timeoutMs)) throw new TimeoutException("Windows did not open the app's audio in time");
                op.GetActivateResult(out int result, out object activated);
                Marshal.ThrowExceptionForHR(result);
                return new AudioClient((IAudioClient)activated);
            }
            finally
            {
                Marshal.ReleaseComObject(op);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>Marks the handler as callable from any thread, which Windows requires of it.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Done.Set();
    }
}
