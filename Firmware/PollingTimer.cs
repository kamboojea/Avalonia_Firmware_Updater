/**
 * @file PollingTimer.cs
 * @author Ali Rahmatinia
 * @brief One-shot polling timer used by the firmware update loop.
 */

using System.Timers;

namespace Amscreen.Timers.Classes
{
    class PollingTimer
    {
        private readonly System.Timers.Timer tmrPollingTimer = new();
        private bool blnPollingTimerExpired;
        private double dblTimerPeriod;

        /**
         * @brief Creates a disabled polling timer.
         */
        public PollingTimer()
        {
            tmrPollingTimer.Elapsed += PollingTimerElapsed;
            tmrPollingTimer.AutoReset = false;
            tmrPollingTimer.Enabled = false;
        }

        /**
         * @brief Creates and starts a polling timer.
         * @param dblPeriod Timer period in milliseconds.
         */
        public PollingTimer(double dblPeriod)
        {
            dblTimerPeriod = dblPeriod;

            if (dblTimerPeriod <= 0)
            {
                return;
            }

            tmrPollingTimer.Elapsed += PollingTimerElapsed;
            tmrPollingTimer.AutoReset = false;
            tmrPollingTimer.Interval = dblTimerPeriod;
            tmrPollingTimer.Enabled = true;
        }

        /**
         * @brief Creates and starts a polling timer.
         * @param dblPeriod Timer period in milliseconds.
         * @param immediate True if the first poll should expire immediately.
         */
        public PollingTimer(double dblPeriod, bool immediate)
        {
            dblTimerPeriod = dblPeriod;

            if (dblTimerPeriod <= 0)
            {
                return;
            }

            tmrPollingTimer.Elapsed += PollingTimerElapsed;
            tmrPollingTimer.AutoReset = false;
            tmrPollingTimer.Interval = dblTimerPeriod;
            tmrPollingTimer.Enabled = true;
            blnPollingTimerExpired = immediate;
        }

        /**
         * @brief Restarts the timer using the current period.
         */
        public void Reset()
        {
            if (dblTimerPeriod <= 0)
            {
                return;
            }

            tmrPollingTimer.Stop();
            blnPollingTimerExpired = false;
            tmrPollingTimer.Start();
        }

        /**
         * @brief Restarts the timer using a new period.
         * @param dblPeriod Timer period in milliseconds.
         */
        public void Reset(double dblPeriod)
        {
            dblTimerPeriod = dblPeriod;

            if (dblTimerPeriod <= 0)
            {
                return;
            }

            tmrPollingTimer.Stop();
            tmrPollingTimer.Interval = dblTimerPeriod;
            blnPollingTimerExpired = false;
            tmrPollingTimer.Start();
        }

        /**
         * @brief Restarts the timer using a new period.
         * @param dblPeriod Timer period in milliseconds.
         * @param immediate True if the next poll should expire immediately.
         */
        public void Reset(double dblPeriod, bool immediate)
        {
            dblTimerPeriod = dblPeriod;

            if (dblTimerPeriod > 0)
            {
                tmrPollingTimer.Stop();
                tmrPollingTimer.Interval = dblTimerPeriod;
                tmrPollingTimer.Start();
            }

            blnPollingTimerExpired = immediate;
        }

        /**
         * @brief Checks if the timer has expired.
         * @return True if the timer has expired.
         */
        public bool Expired()
        {
            if (dblTimerPeriod == 0)
            {
                return true;
            }

            if (!blnPollingTimerExpired)
            {
                return false;
            }

            tmrPollingTimer.Stop();
            tmrPollingTimer.Start();
            blnPollingTimerExpired = false;
            return true;
        }

        private void PollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            blnPollingTimerExpired = true;
        }
    }
}
