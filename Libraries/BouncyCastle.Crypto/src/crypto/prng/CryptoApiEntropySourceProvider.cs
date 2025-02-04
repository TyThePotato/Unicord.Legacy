﻿#if !(NETCF_1_0 || PORTABLE)
using System;
using System.Security.Cryptography;

namespace Org.BouncyCastle.Crypto.Prng
{
    public class CryptoApiEntropySourceProvider
        :   IEntropySourceProvider
    {
        private readonly RandomNumberGenerator mRng;
        private readonly bool mPredictionResistant;

        public CryptoApiEntropySourceProvider()
            : this(new System.Security.Cryptography.RandomNumberGenerator(), true)
        {
        }

        public CryptoApiEntropySourceProvider(RandomNumberGenerator rng, bool isPredictionResistant)
        {
            if (rng == null)
                throw new ArgumentNullException("rng");

            mRng = rng;
            mPredictionResistant = isPredictionResistant;
        }

        public IEntropySource Get(int bitsRequired)
        {
            return new CryptoApiEntropySource(mRng, mPredictionResistant, bitsRequired);
        }

        private class CryptoApiEntropySource
            :   IEntropySource
        {
            private readonly RandomNumberGenerator mRng;
            private readonly bool mPredictionResistant;
            private readonly int mEntropySize;

            internal CryptoApiEntropySource(RandomNumberGenerator rng, bool predictionResistant, int entropySize)
            {
                this.mRng = rng;
                this.mPredictionResistant = predictionResistant;
                this.mEntropySize = entropySize;
            }

            #region IEntropySource Members

            bool IEntropySource.IsPredictionResistant
            {
                get { return mPredictionResistant; }
            }

            byte[] IEntropySource.GetEntropy()
            {
                byte[] result = new byte[(mEntropySize + 7) / 8];
                mRng.GetBytes(result);
                return result;
            }

            int IEntropySource.EntropySize
            {
                get { return mEntropySize; }
            }

            #endregion
        }
    }
}

#endif
