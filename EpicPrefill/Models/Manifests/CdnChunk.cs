namespace EpicPrefill.Models.Manifests
{
    public struct CdnChunk
    {
        /// <summary>
        ///  The primary identifier of a Chunk, represented as 32 character hex string.  Is a 16 byte array, that appears to be an MD5.
        /// </summary>
        [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Guid is its actual name in Epic's data structure")]
        public string Guid;

        /// <summary>
        /// The chunk's hash represented by 16 character hex string.,
        /// </summary>
        public string Hash;

        /// <summary>
        /// Used as part of the download url.  Unsure of its meaning.
        /// </summary>
        public byte GroupNum;

        /// <summary>
        /// The download size of the chunk.
        /// </summary>
        public ulong CompressedFileSize;

        // ChunksV4 is usually determined by the manifest version, however it seems it that it never changes since the manifests are always 18
        public string Uri => Path.Join("ChunksV4", GroupNum.ToString("D2"), $"{Hash}_{Guid}.chunk");

        public override string ToString()
        {
            return Guid;
        }
    }
}