using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TabularEditor.UI
{
    // Keep WinForms scroll events and keyboard handling, with dark painting during mouse tracking.
    internal sealed class DarkScrollBar : NativeWindow, IDisposable
    {
        private const int Paint = 0x000F, EraseBackground = 0x0014, PrintClient = 0x0318;
        private const int SetPosition = 0x00E0, SetRange = 0x00E2, SetRangeAndRedraw = 0x00E6, SetScrollInfo = 0x00E9;
        private const int MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftDoubleClick = 0x0203, CaptureChanged = 0x0215;
        private static readonly Brush ThumbBrush = new SolidBrush(Color.FromArgb(116, 134, 152));
        private static readonly Brush ActiveThumbBrush = new SolidBrush(Color.FromArgb(145, 166, 188));
        private static readonly Pen ArrowPen = new Pen(AppTheme.Muted);
        private static readonly Action<ScrollBar, ScrollEventArgs> RaiseScroll =
            (Action<ScrollBar, ScrollEventArgs>)Delegate.CreateDelegate(typeof(Action<ScrollBar, ScrollEventArgs>),
                typeof(ScrollBar).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic));
        private readonly ScrollBar scrollBar;
        private readonly bool vertical;
        private Timer repeatTimer;
        private bool disposed;
        private bool tracking;
        private int dragOffset;
        private int pointer;
        private ScrollEventType pressedPart;

        internal DarkScrollBar(ScrollBar scrollBar)
        {
            this.scrollBar = scrollBar;
            vertical = scrollBar is VScrollBar;
            scrollBar.HandleCreated += Attach;
            scrollBar.HandleDestroyed += StopTracking;
            scrollBar.Disposed += OnDisposed;
            scrollBar.ValueChanged += Redraw;
            if (scrollBar.IsHandleCreated) Attach(scrollBar, EventArgs.Empty);
        }

        private void Attach(object sender, EventArgs e)
        {
            AssignHandle(scrollBar.Handle);
            scrollBar.Invalidate();
        }

        private void Redraw(object sender, EventArgs e) => scrollBar.Invalidate();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            scrollBar.HandleCreated -= Attach;
            scrollBar.HandleDestroyed -= StopTracking;
            scrollBar.Disposed -= OnDisposed;
            scrollBar.ValueChanged -= Redraw;
            StopTracking(null, EventArgs.Empty);
            if (scrollBar.IsHandleCreated && scrollBar.Capture) scrollBar.Capture = false;
            repeatTimer?.Dispose();
            ReleaseHandle();
            if (!scrollBar.IsDisposed) scrollBar.Invalidate();
        }

        private void OnDisposed(object sender, EventArgs e) => Dispose();

        protected override void WndProc(ref Message message)
        {
            switch (message.Msg)
            {
                case EraseBackground:
                    message.Result = new IntPtr(1);
                    return;
                case Paint:
                    DarkControlPaint.Paint(Handle, scrollBar.ClientRectangle, Draw);
                    return;
                case PrintClient:
                    if (message.WParam != IntPtr.Zero)
                        using (var graphics = Graphics.FromHdc(message.WParam)) Draw(graphics);
                    return;
                case LeftDown:
                case LeftDoubleClick:
                    if (scrollBar.Enabled) StartTracking(MousePositionAlongBar(message.LParam));
                    return;
                case MouseMove when tracking:
                    pointer = MousePositionAlongBar(message.LParam);
                    if (pressedPart == ScrollEventType.ThumbTrack) DragThumb();
                    return;
                case LeftUp when tracking:
                    EndTracking();
                    return;
                case CaptureChanged when tracking:
                    EndTracking();
                    break;
                case SetScrollInfo:
                case SetPosition:
                case SetRangeAndRedraw:
                    // Preserve native scrolling state while coalescing paint requests.
                    // Otherwise the scrollbar can draw the system theme directly during a fast scroll.
                    var update = message;
                    if (message.Msg == SetScrollInfo) update.WParam = IntPtr.Zero;
                    else if (message.Msg == SetPosition) update.LParam = IntPtr.Zero;
                    else update.Msg = SetRange;
                    base.WndProc(ref update);
                    message.Result = update.Result;
                    scrollBar.Invalidate();
                    return;
                default:
                    base.WndProc(ref message);
                    break;
            }
        }

        private int MousePositionAlongBar(IntPtr coordinates)
        {
            var position = unchecked((int)coordinates.ToInt64());
            return vertical ? (short)(position >> 16) : (short)position;
        }

        private bool Mirrored => !vertical && scrollBar.RightToLeft == RightToLeft.Yes;

        private int MaximumValue => (int)Math.Max(scrollBar.Minimum, (long)scrollBar.Maximum - Math.Max(1, scrollBar.LargeChange) + 1);

        private (int Length, int Arrow, int Start, int Size) GetThumb()
        {
            var length = vertical ? scrollBar.ClientSize.Height : scrollBar.ClientSize.Width;
            var thickness = vertical ? scrollBar.ClientSize.Width : scrollBar.ClientSize.Height;
            var arrow = Math.Min(thickness, Math.Max(0, (length - 8) / 2));
            var track = Math.Max(0, length - 2 * arrow);
            var range = Math.Max(1L, (long)scrollBar.Maximum - scrollBar.Minimum + 1);
            var size = Math.Min(track, Math.Max(16, (int)Math.Round(track * (Math.Max(1, scrollBar.LargeChange) / (double)range))));
            var travel = track - size;
            var span = (long)MaximumValue - scrollBar.Minimum;
            var ratio = span == 0 ? 0 : ((long)scrollBar.Value - scrollBar.Minimum) / (double)span;
            ratio = Math.Max(0, Math.Min(1, ratio));
            var start = arrow + (int)Math.Round(travel * (Mirrored ? 1 - ratio : ratio));
            return (length, arrow, start, size);
        }

        private ScrollEventType HitTest(int position)
        {
            var thumb = GetThumb();
            if (position < thumb.Arrow) return Mirrored ? ScrollEventType.SmallIncrement : ScrollEventType.SmallDecrement;
            if (position >= thumb.Length - thumb.Arrow) return Mirrored ? ScrollEventType.SmallDecrement : ScrollEventType.SmallIncrement;
            if (position < thumb.Start) return Mirrored ? ScrollEventType.LargeIncrement : ScrollEventType.LargeDecrement;
            if (position >= thumb.Start + thumb.Size) return Mirrored ? ScrollEventType.LargeDecrement : ScrollEventType.LargeIncrement;
            return ScrollEventType.ThumbTrack;
        }

        private void StartTracking(int position)
        {
            if (MaximumValue <= scrollBar.Minimum) return;
            pointer = position;
            pressedPart = HitTest(position);
            tracking = true;
            scrollBar.Capture = true;
            if (pressedPart == ScrollEventType.ThumbTrack)
                dragOffset = position - GetThumb().Start;
            else
            {
                if (repeatTimer == null)
                {
                    repeatTimer = new Timer();
                    repeatTimer.Tick += Repeat;
                }
                Step();
                if (tracking)
                {
                    repeatTimer.Interval = 400;
                    repeatTimer.Start();
                }
            }
            scrollBar.Invalidate();
        }

        private void Repeat(object sender, EventArgs e)
        {
            repeatTimer.Interval = 55;
            var thumb = GetThumb();
            if (pointer >= 0 && pointer < thumb.Length && HitTest(pointer) == pressedPart) Step();
        }

        private void Step()
        {
            var change = pressedPart == ScrollEventType.SmallIncrement || pressedPart == ScrollEventType.SmallDecrement
                ? scrollBar.SmallChange : scrollBar.LargeChange;
            var direction = pressedPart == ScrollEventType.SmallIncrement || pressedPart == ScrollEventType.LargeIncrement ? 1 : -1;
            ScrollTo(pressedPart, (long)scrollBar.Value + direction * change);
        }

        private void DragThumb()
        {
            var thumb = GetThumb();
            var travel = thumb.Length - 2 * thumb.Arrow - thumb.Size;
            if (travel <= 0) return;

            var offset = Math.Max(0, Math.Min(travel, pointer - dragOffset - thumb.Arrow));
            if (Mirrored) offset = travel - offset;
            var span = (long)MaximumValue - scrollBar.Minimum;
            var value = scrollBar.Minimum + (long)Math.Round(span * (offset / (double)travel));
            if (value != scrollBar.Value) ScrollTo(ScrollEventType.ThumbTrack, value);
        }

        private void EndTracking()
        {
            var wasDragging = pressedPart == ScrollEventType.ThumbTrack;
            StopTracking(null, EventArgs.Empty);
            if (scrollBar.Capture) scrollBar.Capture = false;
            if (wasDragging) ScrollTo(ScrollEventType.ThumbPosition, scrollBar.Value);
            ScrollTo(ScrollEventType.EndScroll, scrollBar.Value);
            scrollBar.Invalidate();
        }

        private void StopTracking(object sender, EventArgs e)
        {
            tracking = false;
            repeatTimer?.Stop();
        }

        private void ScrollTo(ScrollEventType type, long requested)
        {
            if (scrollBar.IsDisposed) return;
            var next = (int)Math.Max(scrollBar.Minimum, Math.Min(MaximumValue, requested));
            var orientation = vertical ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll;
            var args = new ScrollEventArgs(type, scrollBar.Value, next, orientation);
            RaiseScroll(scrollBar, args);
            if (!scrollBar.IsDisposed)
                scrollBar.Value = Math.Max(scrollBar.Minimum, Math.Min(MaximumValue, args.NewValue));
        }

        private void Draw(Graphics graphics)
        {
            var bounds = scrollBar.ClientRectangle;
            graphics.FillRectangle(AppTheme.RaisedBrush, bounds);
            var thumb = GetThumb();
            var thickness = vertical ? bounds.Width : bounds.Height;
            if (thumb.Length <= 0 || thickness < 7) return;

            if (thumb.Size > 0)
            {
                var grip = vertical
                    ? new Rectangle(3, thumb.Start, thickness - 6, thumb.Size)
                    : new Rectangle(thumb.Start, 3, thumb.Size, thickness - 6);
                graphics.FillRectangle(tracking && pressedPart == ScrollEventType.ThumbTrack ? ActiveThumbBrush : ThumbBrush, grip);
            }

            if (thumb.Arrow < 8) return;
            DrawArrow(graphics, thickness / 2, thumb.Arrow / 2, -1);
            DrawArrow(graphics, thickness / 2, thumb.Length - thumb.Arrow / 2, 1);
        }

        private void DrawArrow(Graphics graphics, int cross, int along, int direction)
        {
            var tip = along + 2 * direction;
            var ends = along - direction;
            var points = vertical
                ? new[] { new Point(cross - 3, ends), new Point(cross, tip), new Point(cross + 3, ends) }
                : new[] { new Point(ends, cross - 3), new Point(tip, cross), new Point(ends, cross + 3) };
            graphics.DrawLines(ArrowPen, points);
        }

    }

    internal static class DarkControlPaint
    {
        internal static void Paint(IntPtr handle, Rectangle bounds, Action<Graphics> draw)
        {
            ValidateRect(handle, IntPtr.Zero);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            using (var target = Graphics.FromHwnd(handle))
            using (var buffer = BufferedGraphicsManager.Current.Allocate(target, bounds))
            {
                draw(buffer.Graphics);
                buffer.Render(target);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ValidateRect(IntPtr hwnd, IntPtr rectangle);
    }
}
