/*

Source: https://github.com/dropbox/dropbox-api-content-hasher/blob/master/csharp/src/DropboxContentHasher.cs

Copyright (c) 2017 Dropbox, Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*/

using System;
using System.Security.Cryptography;
using System.Text;

namespace DropboxEncrypedUploader;

/// <summary>
/// Computes a hash using the same algorithm that the Dropbox API uses for the
/// the "content_hash" metadata field.
/// </summary>
///
/// <para>
/// The {@link #digest()} method returns a raw binary representation of the hash.
/// The "content_hash" field in the Dropbox API is a hexadecimal-encoded version
/// of the digest.
/// </para>
///
/// <example>
/// var hasher = new DropboxContentHasher();
/// byte[] buf = new byte[1024];
/// using (var file = File.OpenRead("some-file"))
/// {
///     while (true)
///     {
///         int n = file.Read(buf, 0, buf.Length);
///         if (n &lt;= 0) break;  // EOF
///         hasher.TransformBlock(buf, 0, n, buf, 0);
///     }
/// }
///
/// hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
/// string hexHash = DropboxContentHasher.ToHex(hasher.Hash);
/// Console.WriteLine(hexHash);
/// </example>
public class DropboxContentHasher(SHA256 overallHasher, SHA256 blockHasher, int blockPos) : HashAlgorithm
{
    private int blockPos = blockPos;

    public const int BLOCK_SIZE = 4 * 1024 * 1024;

    public DropboxContentHasher() : this(SHA256.Create(), SHA256.Create(), 0)
    {
    }

    public override int HashSize
    {
        get { return overallHasher.HashSize; }
    }

    protected override void HashCore(byte[] input, int offset, int len)
    {
        int inputEnd = offset + len;
        while (offset < inputEnd)
        {
            if (blockPos == BLOCK_SIZE)
            {
                FinishBlock();
            }

            int spaceInBlock = BLOCK_SIZE - blockPos;
            int inputPartEnd = Math.Min(inputEnd, offset + spaceInBlock);
            int inputPartLength = inputPartEnd - offset;
            blockHasher.TransformBlock(input, offset, inputPartLength, input, offset);

            blockPos += inputPartLength;
            offset += inputPartLength;
        }
    }

    protected override byte[] HashFinal()
    {
        if (blockPos > 0)
        {
            FinishBlock();
        }

        overallHasher.TransformFinalBlock([], 0, 0);
        return overallHasher.Hash;
    }

    public override void Initialize()
    {
        blockHasher.Initialize();
        overallHasher.Initialize();
        blockPos = 0;
    }

    private void FinishBlock()
    {
        blockHasher.TransformFinalBlock([], 0, 0);
        byte[] blockHash = blockHasher.Hash;
        blockHasher.Initialize();

        overallHasher.TransformBlock(blockHash, 0, blockHash.Length, blockHash, 0);
        blockPos = 0;
    }

    private const string HEX_DIGITS = "0123456789abcdef";

    /// <summary>
    /// A convenience method to convert a byte array into a hexadecimal string.
    /// </summary>
    public static string ToHex(byte[] data)
    {
        var r = new StringBuilder();
        foreach (byte b in data)
        {
            r.Append(HEX_DIGITS[b >> 4]);
            r.Append(HEX_DIGITS[b & 0xF]);
        }

        return r.ToString();
    }

    /// <summary>
    /// Computes the Dropbox content hash for a buffer.
    /// Returns the hash as a hexadecimal string.
    /// </summary>
    public static string ComputeHash(byte[] buffer, int length)
    {
        using var hasher = new DropboxContentHasher();
        hasher.TransformBlock(buffer, 0, length, buffer, 0);
        hasher.TransformFinalBlock([], 0, 0);
        return ToHex(hasher.Hash);
    }
}