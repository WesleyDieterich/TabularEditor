using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TabularEditor.UI.Extensions
{
    public static class CueBannerExtension
    {
        private const int EM_SETCUEBANNER = 0x1501;
        private const int EM_GETCUEBANNER = 0x1502;
        private const int CB_SETCUEBANNER = 0x1703;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern Int32 SendMessage(IntPtr hWnd, int msg, int wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);

        public static void SetCueBanner(this Control control, string cueBannerText) =>
            control.SetCueBanner(cueBannerText, false);

        public static void SetCueBanner(this Control control, string cueBannerText, bool showWhileFocused)
        {
            var isComboBox = control is ComboBox;
            SendMessage(control.Handle, isComboBox ? CB_SETCUEBANNER : EM_SETCUEBANNER,
                !isComboBox && showWhileFocused ? 1 : 0, cueBannerText);
        }
    }
}
