﻿using System;
using System.Threading;

using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace Org.BouncyCastle.Crypto.Tls
{
    internal abstract class AbstractTlsContext
        : TlsContext
    {
        private static long counter = Times.NanoTime();

#if NETCF_1_0 || PORTABLE
        private static object counterLock = new object();
        private static long NextCounterValue()
        {
            lock (counterLock)
            {
                return ++counter;
            }
        }
#else
        private static long NextCounterValue()
        {
            return counter++; // god forgive me
            //return Interlocked.Increment(ref counter);
        }
#endif

        private static IRandomGenerator CreateNonceRandom(SecureRandom secureRandom, int connectionEnd)
        {
            byte[] additionalSeedMaterial = new byte[16];
            Pack.UInt64_To_BE((ulong)NextCounterValue(), additionalSeedMaterial, 0);
            Pack.UInt64_To_BE((ulong)Times.NanoTime(), additionalSeedMaterial, 8);
            additionalSeedMaterial[0] &= 0x7F;
            additionalSeedMaterial[0] |= (byte)(connectionEnd << 7);

            IDigest digest = TlsUtilities.CreateHash(HashAlgorithm.sha256);

            byte[] seed = new byte[digest.GetDigestSize()];
            secureRandom.NextBytes(seed);

            IRandomGenerator nonceRandom = new DigestRandomGenerator(digest);
            nonceRandom.AddSeedMaterial(additionalSeedMaterial);
            nonceRandom.AddSeedMaterial(seed);
            return nonceRandom;
        }

        private readonly IRandomGenerator mNonceRandom;
        private readonly SecureRandom mSecureRandom;
        private readonly SecurityParameters mSecurityParameters;

        private ProtocolVersion mClientVersion = null;
        private ProtocolVersion mServerVersion = null;
        private TlsSession mSession = null;
        private object mUserObject = null;

        internal AbstractTlsContext(SecureRandom secureRandom, SecurityParameters securityParameters)
        {
            this.mSecureRandom = secureRandom;
            this.mSecurityParameters = securityParameters;
            this.mNonceRandom = CreateNonceRandom(secureRandom, securityParameters.Entity);
        }

        public virtual IRandomGenerator NonceRandomGenerator
        {
            get { return mNonceRandom; }
        }

        public virtual SecureRandom SecureRandom
        {
            get { return mSecureRandom; }
        }

        public virtual SecurityParameters SecurityParameters
        {
            get { return mSecurityParameters; }
        }

        public abstract bool IsServer { get; }

        public virtual ProtocolVersion ClientVersion
        {
            get { return mClientVersion; }
        }

        internal virtual void SetClientVersion(ProtocolVersion clientVersion)
        {
            this.mClientVersion = clientVersion;
        }

        public virtual ProtocolVersion ServerVersion
        {
            get { return mServerVersion; }
        }

        internal virtual void SetServerVersion(ProtocolVersion serverVersion)
        {
            this.mServerVersion = serverVersion;
        }

        public virtual TlsSession ResumableSession
        {
            get { return mSession; }
        }

        internal virtual void SetResumableSession(TlsSession session)
        {
            this.mSession = session;
        }

        public virtual object UserObject
        {
            get { return mUserObject; }
            set { this.mUserObject = value; }
        }

        public virtual byte[] ExportKeyingMaterial(string asciiLabel, byte[] context_value, int length)
        {
            if (context_value != null && !TlsUtilities.IsValidUint16(context_value.Length))
                throw new ArgumentException("must have length less than 2^16 (or be null)", "context_value");

            SecurityParameters sp = SecurityParameters;
            if (!sp.IsExtendedMasterSecret)
            {
                /*
                 * RFC 7627 5.4. If a client or server chooses to continue with a full handshake without
                 * the extended master secret extension, [..] the client or server MUST NOT export any
                 * key material based on the new master secret for any subsequent application-level
                 * authentication. In particular, it MUST disable [RFC5705] [..].
                 */
                throw new InvalidOperationException("cannot export keying material without extended_master_secret");
            }

            byte[] cr = sp.ClientRandom, sr = sp.ServerRandom;

            int seedLength = cr.Length + sr.Length;
            if (context_value != null)
            {
                seedLength += (2 + context_value.Length);
            }

            byte[] seed = new byte[seedLength];
            int seedPos = 0;

            Array.Copy(cr, 0, seed, seedPos, cr.Length);
            seedPos += cr.Length;
            Array.Copy(sr, 0, seed, seedPos, sr.Length);
            seedPos += sr.Length;
            if (context_value != null)
            {
                TlsUtilities.WriteUint16(context_value.Length, seed, seedPos);
                seedPos += 2;
                Array.Copy(context_value, 0, seed, seedPos, context_value.Length);
                seedPos += context_value.Length;
            }

            if (seedPos != seedLength)
                throw new InvalidOperationException("error in calculation of seed for export");

            return TlsUtilities.PRF(this, sp.MasterSecret, asciiLabel, seed, length);
        }
    }
}
