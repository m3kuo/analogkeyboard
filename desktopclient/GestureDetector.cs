using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Woot_verlay

{
    public class GestureDetector
    {
        public void ProcessPressureProfile(Dictionary<(int row, int col), float> keyPressures)
        {
            GestureType detected = DetectGesture(keyPressures);
            if (detected != GestureType.None)
            {
                ShowGesturePopup(detected);
            }
        }

        public GestureType DetectGesture(Dictionary<(int row, int col), float> keyPressures)
        {
            if (keyPressures.Count == 0) return GestureType.None;

            var activeKeys = keyPressures.Where(kvp => kvp.Value > 0.2f).ToList();

            int rows = activeKeys.Select(k => k.Key.row).Distinct().Count();
            int cols = activeKeys.Select(k => k.Key.col).Distinct().Count();

            float avgPressure = activeKeys.Average(k => k.Value);

            // Flat Palm: wide, moderate uniform
            if (rows >= 2 && rows <= 3 &&
                cols >= 3 && cols <= 5 &&
                avgPressure >= 0.3f && avgPressure <= 0.6f)
            {
                return GestureType.FlatPalm;
            }

            // Fist: tight cluster, high pressure
            if (rows <= 2 &&
                cols <= 2 &&
                avgPressure >= 0.7f)
            {
                return GestureType.Fist;
            }

            // Side of Hand: narrow band, moderate-high
            if ((rows >= 3 && cols == 1) || (cols >= 3 && rows == 1))
            {
                if (avgPressure >= 0.4f && avgPressure <= 0.8f)
                {
                    return GestureType.SideOfHand;
                }
            }

            return GestureType.None;
        }

        private void ShowGesturePopup(GestureType gesture)
        {
            string message = gesture switch
            {
                GestureType.FlatPalm => "Flat Palm gesture detected!",
                GestureType.Fist => "Fist gesture detected!",
                GestureType.SideOfHand => "Side of Hand gesture detected!",
                _ => ""
            };

            if (!string.IsNullOrEmpty(message))
            {
                MessageBox.Show(message, "Gesture Detected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
