﻿using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Text;
using System.Diagnostics;
using System.ComponentModel;


namespace Timeline
{
	public class TimelineGrid : UserControl
	{
		#region Members

		// member variables - rows and elements
		private TimelineElementCollection m_selectedElements;	// the currently selected elementes in the control
		private TimelineRowCollection m_rows;					// all rows displayed in the control

		// member variables - dragging, snapping, etc.
		private const int DragThreshold = 4;					// how many pixels the mouse must move before the elements start dragging
		private DragState m_dragState = DragState.Normal;		// the current dragging state
		private Point m_oldLoc;									// the location of the mouse at last draw; used to update the dragging
		private TimelineElement m_mouseDownElement = null;		// the element under the cursor on a mouse click
		private SortedDictionary<int, Tuple<int, int>> m_snapPixels;	// a mapping of pixel location to a tuple of <snapped pixel location, snap level/priority>
		private SortedDictionary<TimeSpan, int> m_snapPoints;	// a collection of the snap points (as TimeSpans) to use in the control

		// Events
		public event EventHandler<ElementEventArgs> ElementDoubleClicked;
		public event EventHandler<MultiElementEventArgs> ElementsMoved;


		// Enumerations
		private enum DragState
		{
			/// <summary>
			/// Not dragging, mouse is up.
			/// </summary>
			Normal = 0,

			/// <summary>
			/// Mouse down, but hasn't moved past threshold yet to be considered dragging
			/// </summary>
			Waiting,

			/// <summary>
			/// Actively dragging objects
			/// </summary>
			Dragging,
		}
		#endregion


		#region Initialization / Setup

		public TimelineGrid()
		{
			this.DoubleBuffered = true;
			this.AutoScroll = true;
			this.SetStyle(ControlStyles.ResizeRedraw, true);

			m_selectedElements = new TimelineElementCollection();
			m_rows = new TimelineRowCollection();

			TotalTime = TimeSpan.FromMinutes(1);
			VisibleTimeSpan = TimeSpan.FromSeconds(10);
			VisibleTimeStart = TimeSpan.FromSeconds(0);

			m_snapPoints = new SortedDictionary<TimeSpan, int>();

			m_rows.RowAdded += new EventHandler<RowAddedOrRemovedEventArgs>(m_rows_RowAdded);
			m_rows.RowRemoved += new EventHandler<RowAddedOrRemovedEventArgs>(m_rows_RowRemoved);
		}

		public void SetDefaultOptions()
		{
			RowSeparatorColor = Color.Black;
			GridlineInterval = TimeSpan.FromSeconds(1.0);
			BorderStyle = BorderStyle.Fixed3D;
			BackColor = Color.FromKnownColor(KnownColor.ControlDark);
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);
			SetDefaultOptions();
		}

		#endregion


		#region Time & Display properties and methods

		/// <summary>
		/// The maximum amount of time represented by this Grid.
		/// </summary>
		public TimeSpan TotalTime { get; set; }

		/// <summary>
		/// The amount of time currently visible. Adjusting this implements zoom along the X (time) axis.
		/// </summary>
		public TimeSpan VisibleTimeSpan { get; set; }

		/// <summary>
		/// The time at the left of the control (the visible beginning).
		/// </summary>
		public TimeSpan VisibleTimeStart
		{
			get { return pixelsToTime(-AutoScrollPosition.X); }
			set
			{
				if (value < TimeSpan.Zero)
					return;

				AutoScrollPosition = new Point(timeToPixels(value), -AutoScrollPosition.Y);
			}
		}

		public TimeSpan VisibleTimeEnd
		{
			get { return VisibleTimeStart + VisibleTimeSpan; }
			set
			{
				VisibleTimeStart = value - VisibleTimeSpan;
			}
		}

		/// <summary>
		/// Gets the amount of time represented by one horizontal pixel.
		/// </summary>
		protected TimeSpan TimePerPixel
		{
			get { return TimeSpan.FromTicks(VisibleTimeSpan.Ticks / Width); }
		}

		private int timeToPixels(TimeSpan t)
		{
			return (int)(t.Ticks / this.TimePerPixel.Ticks);
		}

		private TimeSpan pixelsToTime(int px)
		{
			return TimeSpan.FromTicks(px * this.TimePerPixel.Ticks);
		}

		#endregion


		#region Row & Element properties and methods

		// TODO JRR 8/16 - Right now, one can use SelectedElements.CollectionChanged to be 
		// notified when the selection changes. However, it seems to be "noisy" - see test app
		// output.  I propse adding a SelectionChanged event which filters this. Or try and
		// clean up the code causing the noisy-ness (if possible).
		public TimelineElementCollection SelectedElements { get { return m_selectedElements; } }
		public TimelineRowCollection Rows { get { return m_rows; } }

		void m_rows_RowAdded(object sender, RowAddedOrRemovedEventArgs e)
		{
			e.Row.ParentControl = this;
		}

		void m_rows_RowRemoved(object sender, RowAddedOrRemovedEventArgs e)
		{
			e.Row.ParentControl = null;
		}

		/// <summary>
		/// Returns the element located at the current point in screen coordinates
		/// </summary>
		/// <param name="p">Screen coordinates</param>
		/// <returns>Element at given point, or null if none exists.</returns>
		protected TimelineElement elementAt(Point p)
		{
			// Translate 
			// First figure out which row we are in
			TimelineRow containingRow = null;
			int curheight = 0;
			foreach (var row in Rows) {
				if (p.Y < curheight + row.Height) {
					containingRow = row;
					break;
				}
				curheight += row.Height;
			}

			if (containingRow == null)
				return null;

			// Now figure out which element we are on
			foreach (var elem in containingRow.Elements) {
				int elemX = timeToPixels(elem.Offset);
				int elemW = timeToPixels(elem.Duration);
				if (p.X >= elemX && p.X <= elemX + elemW)
					return elem;
			}

			return null;
		}


		public TimelineElementCollection ElementsAtTime(TimeSpan time)
		{
			TimelineElementCollection col = new TimelineElementCollection();
			foreach (var row in Rows) {
				foreach (var elem in row.Elements) {
					if ((time >= elem.Offset) && (time <= (elem.Offset + elem.Duration)))
						col.Add(elem);
				}
			}

			return col;
		}

		#endregion


		#region UI: Keypresses, Mouse events, Selecting, Dragging, Snapping

		private bool CtrlPressed { get { return Form.ModifierKeys.HasFlag(Keys.Control); } }

		protected override void OnKeyPress(KeyPressEventArgs e)
		{
			if (e.KeyChar == (char)27)  // ESC
            {
				SelectedElements.Clear();
				endDrag();  // do this regardless of if we're dragging or not.

				this.Refresh();
			}
		}

		private void dragWait(Point location)
		{
			m_dragState = DragState.Waiting;
			m_oldLoc = location;

			// calculate all the snap points (in pixels) for all selected elements
			// for every visible drag point (and a width either side, so they can snap
			// to non-visible points that are close)
			m_snapPixels = new SortedDictionary<int, Tuple<int, int>>();

			foreach (KeyValuePair<TimeSpan, int> kvp in m_snapPoints) {
				if ((kvp.Key >= VisibleTimeStart - VisibleTimeSpan) &&
					(kvp.Key <= VisibleTimeEnd + VisibleTimeSpan)) {

					int snapTimePixelCentre = timeToPixels(kvp.Key);
					int snapRange = kvp.Value;
					int snapLevel = kvp.Value;

					foreach (TimelineElement element in m_selectedElements) {
						int elementPixelStart = timeToPixels(element.Offset);
						int elementPixelEnd = timeToPixels(element.Offset + element.Duration);

						// iterate through all pixels for this particular snap point, for this element
						//for (int curPixel = snapTimePixelCentre - snapRange; curPixel < snapTimePixelCentre + snapRange; curPixel++) {
						for (int offset = -snapRange; offset <= snapRange; offset++) {

							// calculate the relative pixel (to the mouse location) for this point
							int rp = location.X + snapTimePixelCentre + offset - elementPixelStart;

							// see if that pixel is already in the snap-map, and if it's of a higher priority
							if (!(m_snapPixels.ContainsKey(rp) && m_snapPixels[rp].Item2 >= snapLevel)) {
								m_snapPixels[rp] = new Tuple<int, int>(rp - offset, snapLevel);
							}

							// do the same for the element end
							rp = location.X + snapTimePixelCentre + offset - elementPixelEnd;
							if (!(m_snapPixels.ContainsKey(rp) && m_snapPixels[rp].Item2 >= snapLevel)) {
								m_snapPixels[rp] = new Tuple<int, int>(rp - offset, snapLevel);
							}
						}
					}
				}
			}
		}

		private void beginDrag()
		{
			m_dragState = DragState.Dragging;
			this.Cursor = Cursors.SizeAll;
		}

		private void endDrag()
		{
			m_dragState = DragState.Normal;
			this.Cursor = Cursors.Default;
		}

		public bool AddSnapTime(TimeSpan time, int level)
		{
			if (m_snapPoints.ContainsKey(time))
				return false;

			m_snapPoints[time] = level;
			return true;
		}

		public bool RemoveSnapTime(TimeSpan time)
		{
			return m_snapPoints.Remove(time);
		}



		/// <summary>
		/// Translates a MouseEventArgs so that its coordinates represent the coordinates on the underlying timeline, taking into account scroll position.
		/// </summary>
		/// <param name="e"></param>
		private void _translateMouseArgs(ref MouseEventArgs e)
		{
			// Translate this location based on the auto scroll position.
			Point p = e.Location;
			p.Offset(-AutoScrollPosition.X, -AutoScrollPosition.Y);

			// Just "fix" it :-)
			e = new MouseEventArgs(e.Button, e.Clicks, p.X, p.Y, e.Delta);
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			_translateMouseArgs(ref e);

			m_mouseDownElement = elementAt(e.Location);

			switch (e.Button) {
				case MouseButtons.Left:
					OnLeftMouseDown(e);
					break;
				case MouseButtons.Middle:
					break;
				case MouseButtons.None:
					break;
				case MouseButtons.Right:
					break;
				case MouseButtons.XButton1:
					break;
				case MouseButtons.XButton2:
					break;
				default:
					break;
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			_translateMouseArgs(ref e);

			switch (e.Button) {
				case MouseButtons.Left:
					OnLeftMouseUp(e);
					break;
				case MouseButtons.Middle:
					break;
				case MouseButtons.None:
					break;
				case MouseButtons.Right:
					break;
				case MouseButtons.XButton1:
					break;
				case MouseButtons.XButton2:
					break;
				default:
					break;
			}
		}


		private void OnLeftMouseDown(MouseEventArgs e)
		{
			// e is already translated.

			if (m_mouseDownElement == null)   // we clicked nothing - clear selection
            {
				SelectedElements.Clear();
			} else    // our mouse is down on something
            {
				if (m_mouseDownElement.IsSelected) {
					// unselect
					if (CtrlPressed)
						SelectedElements.Remove(m_mouseDownElement);
				} else {
					// select
					if (!CtrlPressed)
						SelectedElements.Clear();
					SelectedElements.AddUnique(m_mouseDownElement);
				}

				dragWait(e.Location);
			}
			this.Refresh();
		}


		private void OnLeftMouseUp(MouseEventArgs e)
		{
			// e is already translated

			if (m_dragState == DragState.Dragging) {
				if (ElementsMoved != null) {
					MultiElementEventArgs evargs = new MultiElementEventArgs();
					evargs.Elements = new TimelineElementCollection();
					foreach (var elem in SelectedElements)
						evargs.Elements.Add(elem);
					ElementsMoved(this, evargs);
				}

			} else {
				// If we're not dragging on mouse up, it could be a click on one of multiple
				// selected elements. (In which case we select only that one)
				if (m_mouseDownElement != null && !CtrlPressed) {
					SelectedElements.Clear();
					SelectedElements.Add(m_mouseDownElement);
				}
			}

			endDrag();  // we always do this, even if we weren't dragging.

			this.Refresh();
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			if (m_dragState == DragState.Normal)
				return;

			_translateMouseArgs(ref e);

			if (m_dragState == DragState.Waiting) {
				if (Math.Abs(e.X - m_oldLoc.X) > DragThreshold)
					beginDrag();
			}
			if (m_dragState == DragState.Dragging) {
				/*
                // Determine if the mouse has moved outside the control.
                int dragOutX = 0;   // How far outside (used for scroll speed)
                if (e.X < 0)
                    dragOutX = e.X;
                else if (e.X > this.Width)
                    dragOutX = e.X - this.Width;

                int dragOutY = 0;   // How far outside (used for scroll speed)
                if (e.Y < 0)
                    dragOutY = e.Y;
                else if (e.Y > this.Height)
                    dragOutY = e.Y - this.Height;

                if (dragOutX != 0 || dragOutY != 0)
                    Debug.WriteLine("dragOutX = {0}   dragOutY = {1}", dragOutX, dragOutY);
                
                
                // Calculate delta to move element
                int dX = e.X - m_oldLoc.X;
                m_oldLoc.X = e.X;
				*/

				int updatedX = e.X;

				// if the cursor position is in a snap location, change the position
				// we update elements to be, to the snapped position
				if (m_snapPixels.ContainsKey(e.X)) {
					updatedX = m_snapPixels[e.X].Item1;
				}

				int dX = updatedX - m_oldLoc.X;
				m_oldLoc.X = updatedX;

				int dY = e.Y - m_oldLoc.Y;
				m_oldLoc.Y = e.Y;

				if (dX != 0) {
					foreach (var elem in SelectedElements) {
						elem.Offset += pixelsToTime(dX);
					}
				}
			}

			this.Refresh();

		}

		protected override void OnMouseClick(MouseEventArgs e)
		{
			//base.OnMouseClick(e);
		}

		protected override void OnMouseDoubleClick(MouseEventArgs e)
		{
			_translateMouseArgs(ref e);

			TimelineElement elem = elementAt(e.Location);

			if (elem != null) {
				if (ElementDoubleClicked != null)
					ElementDoubleClicked(this, new ElementEventArgs() { Element = elem });
			} else {
				// Raise the base class event, b/c the control was clicked, not an element in it.
				base.OnMouseDoubleClick(e);
			}
		}

		#endregion


		#region Drawing methods & properties

		public Color RowSeparatorColor { get; set; }
		public TimeSpan GridlineInterval { get; set; }

		private int _drawRows(Graphics g)
		{
			// Draw row separators
			int curY = 0;
			Pen p = new Pen(RowSeparatorColor);
			foreach (var row in Rows) {
				curY += row.Height;
				Point left = new Point((-AutoScrollPosition.X), curY);
				Point right = new Point((-AutoScrollPosition.X) + Width, curY);
				g.DrawLine(p, left, right);
			}

			return curY;
		}

		private void _drawGridlines(Graphics g)
		{
			// Draw vertical gridlines
			int interval = timeToPixels(GridlineInterval);

			// calculate first tick - (it is the first multiple of interval greater than start)
			// believe it or not, this math is correct :-)
			int start = (-AutoScrollPosition.X) / interval * interval + interval;

			for (int x = start; x < start + Width; x += interval) {
				Pen p = new Pen(Color.Black);
				p.DashStyle = DashStyle.Dash;
				g.DrawLine(p, x, (-AutoScrollPosition.Y), x, (-AutoScrollPosition.Y) + Height);

			}
		}

		private void _drawSnapPoints(Graphics g)
		{
			// iterate through all snap points, and if it's visible, draw it
			foreach (KeyValuePair<TimeSpan, int> kvp in m_snapPoints) {
				int px = timeToPixels(kvp.Key);
				if (kvp.Key >= VisibleTimeStart && kvp.Key < VisibleTimeEnd) {
					int x = timeToPixels(kvp.Key);
					Pen p = new Pen(Color.Blue);
					p.DashPattern = new float[] { kvp.Value, kvp.Value };
					g.DrawLine(p, x, 0, x, Height);
				}
			}
		}

		private void _drawElements(Graphics g)
		{
			// Draw each row
			int top = 0;    // y-coord of top of current row
			foreach (var row in Rows) {
				// Draw each element
				foreach (var element in row.Elements) {
                    DrawElementOptions options = DrawElementOptions.Normal;

                    if (SelectedElements.Contains(element))
                        options |= DrawElementOptions.Selected;

                    Point location = new Point(timeToPixels(element.Offset), top);
                    Size size = new Size(timeToPixels(element.Duration), row.Height);

                    // The rectangle where this element will be drawn
                    Rectangle dstRect = new Rectangle(location, size);

                    // The rectangle this element will draw itself in
                    Rectangle srcRect = new Rectangle(new Point(0, 0), size);

                    // Perform the transformation and save the state.
                    GraphicsContainer containerState = g.BeginContainer(dstRect, srcRect, GraphicsUnit.Pixel);

                    // Prevent the element from drawing outside its bounds
                    g.Clip = new System.Drawing.Region(srcRect);
                    
                    element.Draw(g, srcRect, options);

                    g.EndContainer(containerState);
				}

				top += row.Height;  // next row starts just below this row
			}
		}




		protected override void OnPaint(PaintEventArgs e)
		{
			try {
				//Region a = e.Graphics.Clip;
				//RectangleF b = e.Graphics.ClipBounds;
				//RectangleF c = e.Graphics.VisibleClipBounds;

				//GraphicsContainer timelineContainer = e.Graphics.BeginContainer(
				//    new Rectangle(0, 0, Width, m_timelineHeight),
				//    new Rectangle(0, 0, Width, Height),
				//    GraphicsUnit.Pixel);

				//Region d = e.Graphics.Clip;
				//RectangleF f = e.Graphics.ClipBounds;
				//RectangleF g = e.Graphics.VisibleClipBounds;

				//_drawTimeline(e.Graphics);

				//e.Graphics.EndContainer(timelineContainer);


				e.Graphics.TranslateTransform(this.AutoScrollPosition.X, this.AutoScrollPosition.Y);

				int totalHeight = _drawRows(e.Graphics);

				AutoScrollMinSize = new Size(timeToPixels(TotalTime), totalHeight);

				_drawGridlines(e.Graphics);
				_drawSnapPoints(e.Graphics);
				_drawElements(e.Graphics);

				base.OnPaint(e);
			} catch (Exception ex) {
				MessageBox.Show("Unhandled Exception while drawing TimelineGrid:\n\n" + ex.Message);
				throw;
			}
		}

		#endregion


	}

    [Flags]
    public enum DrawElementOptions
    {
        Normal = 0x0,
        Selected = 0x1,
    }
}
