using System;
using System.IO;
using System.Security.Cryptography;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// A seekable in-memory <see cref="Stream"/> that WTelegramClient uses as its session store,
/// persisting its contents to disk DPAPI-encrypted (CurrentUser) — the same protection
/// CredentialsService uses for api-credentials.json. WTelegramClient rewrites the whole
/// session on change, so persisting on every Write/Flush is sufficient.
/// </summary>
public sealed class DpapiSessionStore : Stream
{
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("CryptoAITerminal.TelegramSession.v1");

    private readonly string _path;
    private readonly MemoryStream _buffer = new();

    public DpapiSessionStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                _buffer.Write(plain, 0, plain.Length);
                _buffer.Position = 0;
            }
            catch
            {
                _buffer.SetLength(0); // corrupt/foreign session → start fresh
            }
        }
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => true;
    public override long Length   => _buffer.Length;
    public override long Position { get => _buffer.Position; set => _buffer.Position = value; }

    public override int  Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin)       => _buffer.Seek(offset, origin);
    public override void SetLength(long value)                      { _buffer.SetLength(value); Persist(); }
    public override void Write(byte[] buffer, int offset, int count){ _buffer.Write(buffer, offset, count); Persist(); }
    public override void Flush()                                    => Persist();

    private void Persist()
    {
        try
        {
            var plain  = _buffer.ToArray();
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var dir    = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(_path, cipher);
        }
        catch { /* best-effort persistence */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Persist();
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
