#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music;

/// <summary>Editable Godot Curve preview. Left click adds/drags points; right click removes interior points.</summary>
public partial class ExpressionCurveGraph : Control
{
	private const float HitRadius = 12f;
	private readonly List<Vector2> _points = new();
	private readonly List<ExpressionSegmentMode> _segments = new();
	private int _dragIndex = -1;
	private int _selectedIndex = -1;
	private ExpressionKind _kind;
	private double _minValue;
	private double _maxValue = 1;
	private double _durationWholeNotes = 1;
	private int _divisionDenominator = 4;
	private bool _snapTime;
	private bool _snapPitch;
	private const float LeftAxisWidth = 34f;
	private const float BottomAxisHeight = 15f;
	private Rect2 PlotRect => new(LeftAxisWidth, 2, Mathf.Max(1, Size.X - LeftAxisWidth - 2), Mathf.Max(1, Size.Y - BottomAxisHeight - 2));
	public event Action<IReadOnlyList<ExpressionPoint>>? PointsChanged;
	public event Action<int, ExpressionPoint>? PointSelected;
	public IReadOnlyList<ExpressionPoint> Points => _points.Select((p, i) => ToExpressionPoint(p, i < _segments.Count ? _segments[i] : ExpressionSegmentMode.Linear)).ToArray();

	public override void _Ready()
	{
		CustomMinimumSize = new Vector2(310, 96);
		ClipContents = true;
		Configure(ExpressionKind.Pitch, -2, 2, 1, 4, true, true);
		SetCurve(ExpressionKind.Pitch, new[] { new ExpressionPoint(0, 0), new ExpressionPoint(1, 0) });
	}

	/// <summary>Sets the value range and musical-time grid; duration is measured in whole-note units.</summary>
	public void Configure(ExpressionKind kind, double minValue, double maxValue, double durationWholeNotes, int divisionDenominator, bool snapTime, bool snapPitch)
	{
		if (maxValue <= minValue) throw new ArgumentOutOfRangeException(nameof(maxValue));
		var values = _points.Select(point => (point.X, ToExpressionPoint(point).Y)).ToArray();
		_kind = kind;
		_minValue = minValue;
		_maxValue = maxValue;
		_durationWholeNotes = Math.Max(.000001, durationWholeNotes);
		_divisionDenominator = Math.Max(1, divisionDenominator);
		_snapTime = snapTime;
		_snapPitch = snapPitch;
		for (int i = 0; i < _points.Count; i++) _points[i] = new Vector2(values[i].X, ToScreenValue((float)values[i].Y));
		QueueRedraw();
	}

	public void SetCurve(ExpressionKind kind, IEnumerable<ExpressionPoint> points)
	{
		_dragIndex = -1;
		_selectedIndex = -1;
		if (_kind != kind)
		{
			var domain = DefaultDomain(kind);
			Configure(kind, domain.Min, domain.Max, _durationWholeNotes, _divisionDenominator, _snapTime, _snapPitch);
		}
		_points.Clear();
		_segments.Clear();
		foreach (var p in points.OrderBy(p => p.X)) { _points.Add(new Vector2((float)p.X, ToScreenValue((float)p.Y))); _segments.Add(p.SegmentMode); }
		if (_points.Count == 0) { _points.AddRange(new[] { new Vector2(0, ToScreenValue(0)), new Vector2(1, ToScreenValue(0)) }); _segments.AddRange(new[] { ExpressionSegmentMode.Linear, ExpressionSegmentMode.Linear }); }
		QueueRedraw();
	}

	public override void _Draw()
	{
		var rect = new Rect2(Vector2.Zero, Size);
		DrawRect(rect, new Color("171c25"));
		var grid = new Color("3c4655");
		var plot = PlotRect;
		var font = GetThemeDefaultFont();
		int fontSize = GetThemeDefaultFontSize();
		int divisions = Math.Max(0, (int)Math.Floor(_durationWholeNotes * _divisionDenominator + 1e-9));
		for (var i = 0; i <= divisions; i++)
		{
			float fraction = (float)(i / (_durationWholeNotes * _divisionDenominator));
			float x = plot.Position.X + plot.Size.X * fraction;
			DrawLine(new Vector2(x, plot.Position.Y), new Vector2(x, plot.End.Y), grid, 1);
			int labelStride = Math.Max(1, (int)Math.Ceiling(divisions / 5.0));
			if (i > 0 && i < divisions && i % labelStride == 0) DrawString(font, new Vector2(x - 3, Size.Y - 2), i.ToString(), HorizontalAlignment.Left, -1, fontSize - 3, new Color("aeb8c6"));
		}
		if (divisions == 0 || Math.Abs(divisions / (_durationWholeNotes * _divisionDenominator) - 1) > .0001)
			DrawLine(new Vector2(plot.End.X, plot.Position.Y), new Vector2(plot.End.X, plot.End.Y), grid, 1);
		bool semitoneDomain = _kind is ExpressionKind.Pitch or ExpressionKind.Vibrato;
		int horizontalLines = semitoneDomain ? Math.Max(0, (int)Math.Floor(_maxValue) - (int)Math.Ceiling(_minValue) + 1) : 5;
		int valueLabelStride = semitoneDomain ? Math.Max(1, (int)Math.Ceiling(horizontalLines / 8.0)) : 1;
		for (int i = 0; i < horizontalLines; i++)
		{
			float value = semitoneDomain ? (float)Math.Ceiling(_minValue) + i : (float)(_minValue + (_maxValue - _minValue) * i / 4.0);
			float fraction = (float)((value - _minValue) / (_maxValue - _minValue));
			float y = plot.End.Y - plot.Size.Y * fraction;
			DrawLine(new Vector2(plot.Position.X, y), new Vector2(plot.End.X, y), grid, 1);
			if (!semitoneDomain || i % valueLabelStride == 0 || i == horizontalLines - 1 || Math.Abs(value) < 0.001)
			{
				string label = semitoneDomain ? value.ToString("+0;-0;0") : value.ToString("0.##");
				DrawString(font, new Vector2(0, y + fontSize * .35f), label, HorizontalAlignment.Left, LeftAxisWidth - 4, fontSize - 3, new Color("aeb8c6"));
			}
		}
		if (_points.Count > 1)
		{
			var color = _kind switch { ExpressionKind.Velocity => new Color(0.25f, 0.9f, 0.35f), ExpressionKind.Pitch => new Color(0.35f, 0.7f, 1f), ExpressionKind.Vibrato => new Color(0.83f, 0.48f, 1f), _ => new Color(1f, 0.66f, 0.25f) };
			DrawLine(ToLocalPoint(new Vector2(0, _points[0].Y)), ToLocalPoint(_points[0]), color, 2f, true);
			for (var i = 0; i < _points.Count - 1; i++)
			{
				var left = _points[i]; var right = _points[i + 1];
				if (_segments[i] == ExpressionSegmentMode.Step)
				{
					var corner = new Vector2(right.X, left.Y);
					DrawLine(ToLocalPoint(left), ToLocalPoint(corner), color, 2f, true);
					DrawLine(ToLocalPoint(corner), ToLocalPoint(right), color, 2f, true);
				}
				else DrawLine(ToLocalPoint(left), ToLocalPoint(right), color, 2f, true);
			}
			DrawLine(ToLocalPoint(_points[^1]), ToLocalPoint(new Vector2(1, _points[^1].Y)), color, 2f, true);
		}
		foreach (var point in _points)
		{
			var position = ToLocalPoint(point);
			DrawCircle(position, 6, new Color("f4d06f"));
			DrawArc(position, 8, 0, Mathf.Tau, 16, new Color("10151d"), 1.5f, true);
		}
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
		{
			if (mouse.Pressed)
			{
				_dragIndex = FindPoint(mouse.Position);
				_selectedIndex = _dragIndex;
				if (_dragIndex < 0)
				{
					var addedPoint = Snap(FromLocalPoint(mouse.Position));
					int duplicate = _points.FindIndex(p => Mathf.Abs(p.X - addedPoint.X) < .0001f);
					if (duplicate >= 0)
					{
						_dragIndex = duplicate;
						_selectedIndex = duplicate;
						PointSelected?.Invoke(duplicate, ToExpressionPoint(_points[duplicate], _segments[duplicate]));
						AcceptEvent();
						return;
					}
					_points.Add(addedPoint); _segments.Add(ExpressionSegmentMode.Linear);
					SortPoints();
					_dragIndex = _points.IndexOf(addedPoint);
					_selectedIndex = _dragIndex;
					Changed();
				}
				else PointSelected?.Invoke(_dragIndex, ToExpressionPoint(_points[_dragIndex], _segments[_dragIndex]));
				AcceptEvent();
			}
			else _dragIndex = -1;
		}
		else if (@event is InputEventMouseMotion motion && _dragIndex >= 0)
		{
			var point = Snap(FromLocalPoint(motion.Position));
			if (_dragIndex == 0) point.X = 0;
			else if (_dragIndex == _points.Count - 1) point.X = 1;
			else if (point.X <= _points[_dragIndex - 1].X + .0001f || point.X >= _points[_dragIndex + 1].X - .0001f)
				point.X = _points[_dragIndex].X;
			_points[_dragIndex] = point;
			Changed();
			AcceptEvent();
		}
		else if (@event is InputEventMouseButton right && right.ButtonIndex == MouseButton.Right && right.Pressed)
		{
			int index = FindPoint(right.Position);
			if (index > 0 && index < _points.Count - 1)
			{
				_points.RemoveAt(index); _segments.RemoveAt(index);
				_selectedIndex = -1;
				_dragIndex = -1;
				Changed();
				PointSelected?.Invoke(-1, new ExpressionPoint(0, 0));
			}
			AcceptEvent();
		}
	}

	public void SetSelectedValue(double value)
	{
		if (_selectedIndex < 0 || _selectedIndex >= _points.Count) return;
		_points[_selectedIndex] = new Vector2(_points[_selectedIndex].X, ToScreenValue((float)value));
		Changed();
	}

	public void SetSelectedSegmentMode(ExpressionSegmentMode mode)
	{
		if (_selectedIndex < 0 || _selectedIndex >= _segments.Count) return;
		_segments[_selectedIndex] = mode;
		Changed();
	}

	private Vector2 ToLocalPoint(Vector2 point) => new(PlotRect.Position.X + point.X * PlotRect.Size.X, PlotRect.End.Y - point.Y * PlotRect.Size.Y);
	private Vector2 FromLocalPoint(Vector2 point) => new(Mathf.Clamp((point.X - PlotRect.Position.X) / PlotRect.Size.X, 0, 1), 1f - Mathf.Clamp((point.Y - PlotRect.Position.Y) / PlotRect.Size.Y, 0, 1));
	private Vector2 Snap(Vector2 point)
	{
		if (_snapTime) point.X = (float)SnapMusicalTime(point.X, _durationWholeNotes, _divisionDenominator);
		if (_snapPitch && (_kind is ExpressionKind.Pitch or ExpressionKind.Vibrato)) point.Y = ToScreenValue(Mathf.Round((float)ToExpressionPoint(point).Y));
		return point;
	}

	/// <summary>Snaps normalized note progress to a whole-note subdivision.</summary>
	public static double SnapMusicalTime(double progress, double durationWholeNotes, int denominator)
	{
		if (!double.IsFinite(progress) || !double.IsFinite(durationWholeNotes) || durationWholeNotes <= 0 || denominator <= 0)
			throw new ArgumentOutOfRangeException(nameof(durationWholeNotes));
		var step = 1.0 / denominator / durationWholeNotes;
		return Math.Clamp(Math.Round(progress / step, MidpointRounding.AwayFromZero) * step, 0, 1);
	}
	private int FindPoint(Vector2 position)
	{
		for (int i = 0; i < _points.Count; i++)
			if (ToLocalPoint(_points[i]).DistanceTo(position) <= HitRadius) return i;
		return -1;
	}
	private void SortPoints()
	{
		var paired = _points.Select((p, i) => (Point: p, Mode: _segments[i])).OrderBy(p => p.Point.X).ToArray();
		_points.Clear(); _segments.Clear();
		foreach (var item in paired) { _points.Add(item.Point); _segments.Add(item.Mode); }
	}
	private void Changed()
	{
		SortPoints(); QueueRedraw();
		PointsChanged?.Invoke(_points.Select((p, i) => ToExpressionPoint(p, _segments[i])).ToArray());
		if (_selectedIndex >= 0 && _selectedIndex < _points.Count) PointSelected?.Invoke(_selectedIndex, ToExpressionPoint(_points[_selectedIndex], _segments[_selectedIndex]));
	}
	private float Sample(float x)
	{
		if (x <= _points[0].X) return _points[0].Y;
		for (int i = 1; i < _points.Count; i++)
		{
			var a = _points[i - 1]; var b = _points[i];
			if (x < b.X) return _segments[i - 1] == ExpressionSegmentMode.Step ? a.Y : Mathf.Lerp(a.Y, b.Y, (x - a.X) / Mathf.Max(.00001f, b.X - a.X));
		}
		return _points[^1].Y;
	}
	private float ToScreenValue(float value) => Mathf.Clamp((value - (float)_minValue) / (float)(_maxValue - _minValue), 0, 1);
	private ExpressionPoint ToExpressionPoint(Vector2 point, ExpressionSegmentMode mode = ExpressionSegmentMode.Linear) => new(point.X, _minValue + point.Y * (_maxValue - _minValue), mode);
	private static (double Min, double Max) DefaultDomain(ExpressionKind kind) => kind switch
	{
		ExpressionKind.Pitch or ExpressionKind.Vibrato => (-2, 2),
		ExpressionKind.Velocity => (0, 2),
		_ => (0, 1)
	};
}
