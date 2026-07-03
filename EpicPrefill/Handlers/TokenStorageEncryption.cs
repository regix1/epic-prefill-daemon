namespace EpicPrefill.Handlers
{
    /// <summary>
    /// Provides AES-256-GCM encryption for token storage on disk.
    /// The encryption key is derived via HKDF from a random key file stored alongside the
    /// account file on the persistent config volume, so it survives container recreation.
    ///
    /// Stored format: "ENC:" + Base64( nonce[12] + ciphertext[N] + tag[16] )
    /// </summary>
    internal static class TokenStorageEncryption
    {
        private const string EncryptedPrefix = "ENC:";
        private const string PurposeLabel = "EpicPrefill.TokenStorage.v1";
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;

        /// <summary>Returns true when <paramref name="fileContent"/> was produced by <see cref="Encrypt"/>.</summary>
        internal static bool IsEncrypted(string fileContent) =>
            fileContent.StartsWith(EncryptedPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Encrypts <paramref name="plaintext"/> and returns a string safe for file storage.
        /// </summary>
        internal static string Encrypt(string plaintext)
        {
            var key = DeriveKey();
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            // Zero out key material immediately after use
            CryptographicOperations.ZeroMemory(key);

            // Layout: nonce | ciphertext | tag — all in one Base64 blob
            var blob = new byte[NonceSize + ciphertext.Length + TagSize];
            nonce.CopyTo(blob, 0);
            ciphertext.CopyTo(blob, NonceSize);
            tag.CopyTo(blob, NonceSize + ciphertext.Length);

            return EncryptedPrefix + System.Convert.ToBase64String(blob);
        }

        /// <summary>
        /// Decrypts a value previously produced by <see cref="Encrypt"/>.
        /// </summary>
        /// <exception cref="CryptographicException">Thrown when the data is tampered or the key does not match.</exception>
        internal static string Decrypt(string encryptedValue)
        {
            if (!encryptedValue.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("Value is not in encrypted format.", nameof(encryptedValue));
            }

            var blob = System.Convert.FromBase64String(encryptedValue[EncryptedPrefix.Length..]);

            if (blob.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("Encrypted blob is too short to be valid.");
            }

            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
            var ciphertext = blob.AsSpan(NonceSize, blob.Length - NonceSize - TagSize);

            var key = DeriveKey();
            var plaintext = new byte[ciphertext.Length];

            try
            {
                using var aesGcm = new AesGcm(key, TagSize);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        /// <summary>
        /// Derives a 256-bit key from a random key file stored alongside the account file on the
        /// persistent config volume, so that decryption survives container recreation.
        /// </summary>
        private static byte[] DeriveKey()
        {
            var ikm = LoadOrCreateKeyFile();
            try
            {
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySize,
                    info: Encoding.UTF8.GetBytes(PurposeLabel));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ikm);
            }
        }

        // Key material lives NEXT TO the account file on the persistent /app/Config volume,
        // so it survives container recreation (hostname does not).
        //
        // Creation is exclusive (FileMode.CreateNew) so two containers racing on the same shared
        // volume can never both "win" and last-writer-wins each other's key - the loser instead reads
        // back whatever the winner wrote. A corrupt (non-Base64), wrong-length, or missing key file is
        // regenerated rather than thrown on; any account file it previously protected becomes
        // undecryptable, which the guarded Decrypt call sites discard.
        private static byte[] LoadOrCreateKeyFile()
        {
            var keyPath = Path.Combine(AppConfig.ConfigDir, "storage.key");

            var existing = TryReadValidKeyFile(keyPath);
            if (existing != null)
            {
                return existing;
            }

            // Either missing, or present-but-invalid (corrupt/wrong-length/torn write). In the invalid
            // case, clear it first so the exclusive create below isn't permanently blocked by known-bad
            // content - safe because we already established that content is unusable to any reader.
            if (File.Exists(keyPath))
            {
                try { File.Delete(keyPath); } catch { /* another writer may already be replacing it */ }
            }

            var key = new byte[KeySize];
            RandomNumberGenerator.Fill(key);

            if (TryCreateKeyFileExclusive(keyPath, key))
            {
                return key;
            }

            // Lost the exclusive-create race to another process/container sharing this volume (or the
            // file appeared between our initial read attempt and here). Defer to whatever the winner
            // wrote so every writer converges on ONE key instead of last-writer-wins; a brief
            // write-in-progress window is covered by a few short retries before giving up.
            CryptographicOperations.ZeroMemory(key);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var winner = TryReadValidKeyFile(keyPath);
                if (winner != null)
                {
                    return winner;
                }
                Thread.Sleep(20);
            }

            throw new InvalidOperationException($"Unable to read or create the token storage key file at '{keyPath}'.");
        }

        /// <summary>
        /// Reads and validates the key file: must be valid Base64 decoding to exactly <see cref="KeySize"/>
        /// bytes. Returns null (never throws) for "file missing", "corrupt Base64", "wrong length"
        /// (e.g. a torn/partial write), "read raced a concurrent writer", or "unreadable due to file
        /// permissions" - all treated identically by the caller (regenerate or retry; the unconditional
        /// delete-before-recreate step in <see cref="LoadOrCreateKeyFile"/> best-effort clears an
        /// unreadable file the same way it clears a corrupt one).
        /// </summary>
        private static byte[]? TryReadValidKeyFile(string keyPath)
        {
            if (!File.Exists(keyPath))
            {
                return null;
            }

            try
            {
                var key = System.Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
                return key.Length == KeySize ? key : null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Atomically creates the key file exclusively (fails if it already exists), so a partial
        /// write is never visible to another reader: either the whole file is created with its full
        /// content, or creation fails and any partially-written bytes are deleted. Returns false
        /// (without throwing) when another writer already created the file first.
        /// </summary>
        private static bool TryCreateKeyFileExclusive(string keyPath, byte[] key)
        {
            var stream = TryOpenKeyFileExclusive(keyPath);
            if (stream == null)
            {
                // Another writer already created the file first (or it appeared between our initial
                // read attempt and here) - not our file to touch; the caller reads back whatever the
                // winner wrote.
                return false;
            }

            try
            {
                using (stream)
                {
                    var bytes = Encoding.UTF8.GetBytes(System.Convert.ToBase64String(key));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
            catch
            {
                // Partial write - remove the corrupt file WE just created rather than leaving it for
                // the next reader to trip over. TryReadValidKeyFile's length/Base64 checks protect
                // every future reader even if this best-effort delete itself fails.
                try { File.Delete(keyPath); } catch { /* best effort */ }
                throw;
            }

            SetRestrictivePermissions(keyPath);
            return true;
        }

        private static FileStream? TryOpenKeyFileExclusive(string keyPath)
        {
            try
            {
                return new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// Sets restrictive file permissions on Unix (equivalent to chmod 600).
        /// On Windows this is a no-op because the token data is already encrypted.
        /// </summary>
        internal static void SetRestrictivePermissions(string filePath)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
