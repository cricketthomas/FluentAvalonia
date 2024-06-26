﻿using Avalonia;
using Avalonia.Controls;
using System.Collections.Specialized;
using System.Diagnostics;

namespace FluentAvalonia.UI.Controls;

internal class FlowLayoutAlgorithm : IOrientationBasedMeasures
{
    public ScrollOrientation ScrollOrientation { get; set; }

    public Rect LastExtent => _lastExtent;

    public void InitializeForContext(VirtualizingLayoutContext context,
        IFlowLayoutAlgorithmDelegates callbacks)
    {
        _algorithmCallbacks = callbacks;
        _context = context;
        _elementManager.SetContext(context);
    }

    public void UninitializeForContext(VirtualizingLayoutContext context)
    {
        if (IsVirtualizingContext())
        {
            // This layout is about to be detached. Let go of all elements
            // being held and remove the layout state from the context.
            _elementManager.ClearRealizedRange();
        }
        context.LayoutStateCore = null;
    }

    public Size Measure(Size availableSize, VirtualizingLayoutContext context,
        bool isWrapping, double minItemSpacing, double lineSpacing,
        int maxItemsPerLine, ScrollOrientation orientation,
        bool disableVirtualization, string layoutId)
    {
        ScrollOrientation = orientation;

        // If minor size is infinity, there is only one line and no need to align that line.
        _scrollOrientationSameAsFlow = double.IsInfinity(this.Minor(availableSize));
        var realizationRect = RealizationRect();
#if DEBUG && REPEATER_TRACE
        Log.Debug("{Layout}: MeasureLayout realization {Rect}", layoutId, realizationRect);
#endif
        var suggestedAnchorIndex = _context.RecommendedAnchorIndex;
        if (_elementManager.IsIndexValidInData(suggestedAnchorIndex))
        {
            var anchorRealized = _elementManager.IsDataIndexRealized(suggestedAnchorIndex);
            if (!anchorRealized)
            {
                MakeAnchor(_context, suggestedAnchorIndex, availableSize);
            }
        }

        if (!disableVirtualization)
        {
            _elementManager.OnBeginMeasure(orientation);
        }

        int anchorIndex = GetAnchorIndex(availableSize, isWrapping,
            minItemSpacing, disableVirtualization, layoutId);
        Generate(GenerateDirection.Forward, anchorIndex, availableSize,
            minItemSpacing, lineSpacing, maxItemsPerLine, disableVirtualization, layoutId);
        Generate(GenerateDirection.Backward, anchorIndex, availableSize,
            minItemSpacing, lineSpacing, maxItemsPerLine, disableVirtualization, layoutId);

        if (isWrapping && IsReflowRequired())
        {
#if DEBUG && REPEATER_TRACE
            Log.Debug("{Layout}: Reflow Pass", layoutId);
#endif
            var firstElementBounds = _elementManager.GetLayoutBoundsForRealizedIndex(0);
            this.SetMinorStart(ref firstElementBounds, 0);
            _elementManager.SetLayoutBoundsForRealizedIndex(0, firstElementBounds);
            Generate(GenerateDirection.Forward, 0 /*anchorIndex*/,
                availableSize, minItemSpacing, lineSpacing, maxItemsPerLine,
                disableVirtualization, layoutId);
        }

        RaiseLineArranged();
        _collectionChangePending = false;
        _lastExtent = EstimateExtent(availableSize, layoutId);
        SetLayoutOrigin();

        return _lastExtent.Size;
    }

    public Size Arrange(Size finalSize, VirtualizingLayoutContext context,
        bool isWrapping, LineAlignment lineAlignment,
        string layoutId)
    {
#if DEBUG && REPEATER_TRACE
        Log.Debug("{Layout}: ArrangeLayout", layoutId);
#endif
        ArrangeVirtualizingLayout(finalSize, lineAlignment, isWrapping, layoutId);

        return new Size(
            Math.Max(finalSize.Width, _lastExtent.Width),
            Math.Max(finalSize.Height, _lastExtent.Height));
    }

    public void MakeAnchor(VirtualizingLayoutContext context, int index, Size availableSize)
    {
        _elementManager.ClearRealizedRange();
        // FlowLayout requires that the anchor is the first element in the row.
        var internalAnchor = _algorithmCallbacks
            .Algorithm_GetAnchorForTargetElement(index, availableSize, context);
        Debug.Assert(internalAnchor.Index <= index);

        // No need to set the position of the anchor.
        // (0,0) is fine for now since the extent can
        // grow in any direction.

        for (int dataIndex = internalAnchor.Index; dataIndex < index + 1; dataIndex++)
        {
            var element = context.GetOrCreateElementAt(dataIndex,
                ElementRealizationOptions.ForceCreate | ElementRealizationOptions.SuppressAutoRecycle);
            element.Measure(_algorithmCallbacks.Algorithm_GetMeasureSize(dataIndex, availableSize, context));
            _elementManager.Add(element, dataIndex);
        }
    }

    public void OnItemsSourceChanged(object source, NotifyCollectionChangedEventArgs args,
        VirtualizingLayoutContext context)
    {
        _elementManager.DataSourceChanged(source, args);
        _collectionChangePending = true;
    }

    public Size MeasureElement(Control element, int index, Size availableSize,
        VirtualizingLayoutContext context)
    {
        var measureSize = _algorithmCallbacks.Algorithm_GetMeasureSize(index, availableSize, context);
        element.Measure(measureSize);
        var provisionalArrangeSize = _algorithmCallbacks
            .Algorithm_GetProvisionalArrangeSize(index, measureSize, element.DesiredSize, context);
        _algorithmCallbacks.Algorithm_OnElementMeasured(
            element, index, availableSize, measureSize, element.DesiredSize, 
            provisionalArrangeSize, context);

        return provisionalArrangeSize;
    }

    private int GetAnchorIndex(Size availableSize, bool isWrapping,
        double minItemSpacing, bool disableVirtualization, string layoutId)
    {
        int anchorIndex = -1;
        Point anchorPosition = default;
        var context = _context;

        if (!IsVirtualizingContext() || disableVirtualization)
        {
            // Non virtualizing host, start generating from the element 0
            anchorIndex = context.ItemCountCore() > 0 ? 0 : -1;
        }
        else
        {
            bool isRealizationWindowConnected = _elementManager
                .IsWindowConnected(RealizationRect(), ScrollOrientation, 
                _scrollOrientationSameAsFlow);
            // Item spacing and size in non-virtualizing direction change can cause elements to reflow
            // and get a new column position. In that case we need the anchor to be positioned in the
            // correct column.
            bool needAnchorColumnRevaluation = isWrapping &&
                (this.Minor(_lastAvailableSize) != this.Minor(availableSize) ||
                _lastItemSpacing != minItemSpacing ||
                _collectionChangePending);

            var suggestedAnchorIndex = _context.RecommendedAnchorIndex;

            bool isAnchorSuggestionValid = suggestedAnchorIndex >= 0 &&
                _elementManager.IsDataIndexRealized(suggestedAnchorIndex);

            if (isAnchorSuggestionValid)
            {
#if DEBUG && REPEATER_TRACE
                Log.Debug("{Layout}: Using suggested anchor {index}", layoutId, suggestedAnchorIndex);
#endif
                anchorIndex = _algorithmCallbacks.Algorithm_GetAnchorForTargetElement(
                    suggestedAnchorIndex, availableSize, context).Index;

                if (_elementManager.IsDataIndexRealized(anchorIndex))
                {
                    var anchorBounds = _elementManager.GetLayoutBoundsForDataIndex(anchorIndex);
                    if (needAnchorColumnRevaluation)
                    {
                        // We were provided a valid anchor, but its position might be incorrect because for example it is in
                        // the wrong column. We do know that the anchor is the first element in the row, so we can force the minor position
                        // to start at 0.
                        anchorPosition = this.MinorMajorPoint(0, this.MajorStart(anchorBounds));
                    }
                    else
                    {
                        anchorPosition = new Point(anchorBounds.X, anchorBounds.Y);
                    }
                }
                else
                {
                    // It is possible to end up in a situation during a collection change where GetAnchorForTargetElement returns an index
                    // which is not in the realized range. Eg. insert one item at index 0 for a grid layout.
                    // SuggestedAnchor will be 1 (used to be 0) and GetAnchorForTargetElement will return 0 (left most item in row). However 0 is not in the
                    // realized range yet. In this case we realize the gap between the target anchor and the suggested anchor.
                    int firstRealizedDataIndex = _elementManager.GetDataIndexFromRealizedRangeIndex(0);
                    Debug.Assert(anchorIndex < firstRealizedDataIndex);
                    for (int i = firstRealizedDataIndex - 1; i >= anchorIndex; i--)
                    {
                        _elementManager.EnsureElementRealized(false /*forward*/, i, layoutId);
                    }

                    var anchorBounds = _elementManager.GetLayoutBoundsForDataIndex(suggestedAnchorIndex);
                    anchorPosition = this.MinorMajorPoint(0, this.MajorStart(anchorBounds));
                }
            }
            else if (needAnchorColumnRevaluation || !isRealizationWindowConnected)
            {
#if DEBUG && REPEATER_TRACE
                if (needAnchorColumnRevaluation)
                    Log.Debug("{Layout}: NeedAnchorColumnRevaluation", layoutId);
                if (!isRealizationWindowConnected)
                    Log.Debug("{Layout}: Disconnected Window", layoutId);
#endif

                // The anchor is based on the realization window because a connected ItemsRepeater might intersect the realization window
                // but not the visible window. In that situation, we still need to produce a valid anchor.
                var anchorInfo = _algorithmCallbacks
                    .Algorithm_GetAnchorForRealizationRect(availableSize, context);
                anchorIndex = anchorInfo.Index;
                anchorPosition = this.MinorMajorPoint(0, anchorInfo.Offset);
            }
            else
            {
#if DEBUG && REPEATER_TRACE
                Log.Debug("{Layout}: Connected window - picking first realized element as anchor", layoutId);
#endif
                // No suggestion - just pick first in realized range
                anchorIndex = _elementManager.GetDataIndexFromRealizedRangeIndex(0);
                var firstElementBounds = _elementManager.GetLayoutBoundsForRealizedIndex(0);
                anchorPosition = new Point(firstElementBounds.X, firstElementBounds.Y);
            }
        }

#if DEBUG && REPEATER_TRACE
        Log.Debug("{Layout}: Picked anchor {index}", layoutId, anchorIndex);
#endif
        Debug.Assert(anchorIndex == -1 || _elementManager.IsIndexValidInData(anchorIndex));
        _firstRealizedDataIndexInsideRealizationWindow = _lastRealizedDataIndexInsideRealizationWindow = anchorIndex;

        if (_elementManager.IsIndexValidInData(anchorIndex))
        {
            if (!_elementManager.IsDataIndexRealized(anchorIndex))
            {
#if DEBUG && REPEATER_TRACE
                Log.Debug("{Layout} Disconnected Window - throwing away all realized elements", layoutId);
#endif
                // Disconnected, throw everything and create new anchor
                _elementManager.ClearRealizedRange();
                var anchor = _context.GetOrCreateElementAt(anchorIndex,
                    ElementRealizationOptions.ForceCreate | ElementRealizationOptions.SuppressAutoRecycle);
                _elementManager.Add(anchor, anchorIndex);
            }

            var anchorElement = _elementManager.GetRealizedElement(anchorIndex);
            var desiredSize = MeasureElement(anchorElement, anchorIndex, availableSize, context);
            var layoutBounds = new Rect(anchorPosition, desiredSize);
            _elementManager.SetLayoutBoundsForDataIndex(anchorIndex, layoutBounds);

#if DEBUG && REPEATER_TRACE
            Log.Debug("{Layout} Layout bounds of anchor {Index} are {Bounds}", layoutId,
                anchorIndex, layoutBounds);
#endif
        }
        else
        {
#if DEBUG && REPEATER_TRACE
            Log.Debug("{Layout} Anchor index is not valid - throwing away all realized elements", layoutId);
#endif
            // Throw everything away
            _elementManager.ClearRealizedRange();
        }

        // TODO: Perhaps we can track changes in the property setter
        _lastAvailableSize = availableSize;
        _lastItemSpacing = minItemSpacing;

        return anchorIndex;
    }

    private void Generate(GenerateDirection direction, int anchorIndex, Size availableSize,
        double minItemSpacing, double lineSpacing, int maxItemsPerLine,
        bool disableVirtualization, string layoutId)
    {
        if (anchorIndex == -1)
            return;

        int step = direction == GenerateDirection.Forward ? 1 : -1;
#if DEBUG && REPEATER_TRACE
        Log.Debug("{LayoutId}: Generating {Direction} from anchor {Index}",
            layoutId, direction, anchorIndex);
#endif
        int previousIndex = anchorIndex;
        int currentIndex = anchorIndex + step;
        var anchorBounds = _elementManager.GetLayoutBoundsForDataIndex(anchorIndex);
        double lineOffset = this.MajorStart(anchorBounds);
        double lineMajorSize = this.MajorSize(anchorBounds);
        int countInLine = 1;
        bool lineNeedsReposition = false;

        while (_elementManager.IsIndexValidInData(currentIndex) &&
            (disableVirtualization || ShouldContinueFillingUpSpace(previousIndex, direction)))
        {
            // Ensure layout element.
            _elementManager.EnsureElementRealized(direction == GenerateDirection.Forward, currentIndex, layoutId);
            var currentElement = _elementManager.GetRealizedElement(currentIndex);
            var desiredSize = MeasureElement(currentElement, currentIndex, availableSize, _context);

            // Lay it out.
            var previousElement = _elementManager.GetRealizedElement(previousIndex);
            Rect currentBounds = new Rect(0, 0, desiredSize.Width, desiredSize.Height);
            var previousElementBounds = _elementManager.GetLayoutBoundsForDataIndex(previousIndex);

            if (direction == GenerateDirection.Forward)
            {
                double remainingSpace = this.Minor(availableSize) -
                    (this.MinorStart(previousElementBounds) + this.MinorSize(previousElementBounds) + minItemSpacing + this.Minor(desiredSize));

                if (countInLine >= maxItemsPerLine || _algorithmCallbacks.Algorithm_ShouldBreakLine(currentIndex, remainingSpace))
                {
                    // No more space in this row. wrap to next row.
                    this.SetMinorStart(ref currentBounds, 0);
                    this.SetMajorStart(ref currentBounds,
                        this.MajorStart(previousElementBounds) + lineMajorSize + lineSpacing);

                    if (lineNeedsReposition)
                    {
                        // reposition the previous line (countInLine items)
                        for (int i = 0; i < countInLine; i++)
                        {
                            var dataIndex = currentIndex - 1 - i;
                            var bounds = _elementManager.GetLayoutBoundsForDataIndex(dataIndex);
                            this.SetMajorSize(ref bounds, lineMajorSize);
                            _elementManager.SetLayoutBoundsForDataIndex(dataIndex, bounds);
                        }
                    }

                    // Setup for next line.
                    lineMajorSize = this.MajorSize(currentBounds);
                    lineOffset = this.MajorStart(currentBounds);
                    lineNeedsReposition = false;
                    countInLine = 1;
                }
                else
                {
                    // More space is available in this row.
                    this.SetMinorStart(ref currentBounds,
                        this.MinorStart(previousElementBounds) + this.MinorSize(previousElementBounds) + minItemSpacing);
                    this.SetMajorStart(ref currentBounds, lineOffset);
                    lineMajorSize = Math.Max(lineMajorSize, this.MajorSize(currentBounds));
                    lineNeedsReposition = this.MajorSize(previousElementBounds) != this.MajorSize(currentBounds);
                    countInLine++;
                }
            }
            else
            {
                // Backward
                double remainingSpace = this.MinorStart(previousElementBounds) -
                    (this.Minor(desiredSize) + minItemSpacing);

                if (countInLine >= maxItemsPerLine || _algorithmCallbacks.Algorithm_ShouldBreakLine(currentIndex, remainingSpace))
                {
                    // Does not fit, wrap to the previous row
                    var availableSizeMinor = this.Minor(availableSize);
                    // If the last available size is finite, start from end and subtract our desired size.
                    // Otherwise, look at the last extent and use that for positioning.
                    this.SetMinorStart(ref currentBounds,
                        !double.IsInfinity(availableSizeMinor) ?
                            availableSizeMinor - this.Minor(desiredSize) :
                            this.MinorSize(LastExtent) - this.Minor(desiredSize));
                    this.SetMajorStart(ref currentBounds,
                        lineOffset - this.Major(desiredSize) - lineSpacing);

                    if (lineNeedsReposition)
                    {
                        var previousLineOffset = this.MajorStart(
                            _elementManager.GetLayoutBoundsForDataIndex(currentIndex + countInLine + 1));
                        // reposition the previous line (countInLine items)
                        for (int i = 0; i < countInLine; i++)
                        {
                            var dataIndex = currentIndex + 1 + i;
                            if (dataIndex != anchorIndex)
                            {
                                var bounds = _elementManager.GetLayoutBoundsForDataIndex(dataIndex);
                                this.SetMajorStart(ref bounds, previousLineOffset - lineMajorSize - lineSpacing);
                                this.SetMajorSize(ref bounds, lineMajorSize);
                                _elementManager.SetLayoutBoundsForDataIndex(dataIndex, bounds);
#if DEBUG && REPEATER_TRACE
                                Log.Debug("{Layout}: Corrected Layout bounds of element {Index} are {X} {Y} {Width} {Height}",
                                    layoutId, dataIndex,
                                    bounds.X, bounds.Y, bounds.Width, bounds.Height);
#endif
                            }
                        }
                    }

                    // Setup for next line.
                    lineMajorSize = this.MajorSize(currentBounds);
                    lineOffset = this.MajorStart(currentBounds);
                    lineNeedsReposition = false;
                    countInLine = 1;
                }
                else
                {
                    // Fits in this row. put it in the previous position
                    this.SetMinorStart(ref currentBounds,
                        this.MinorStart(previousElementBounds) - this.Minor(desiredSize) - minItemSpacing);
                    this.SetMajorStart(ref currentBounds, lineOffset);
                    lineMajorSize = Math.Max(lineMajorSize, this.MajorSize(currentBounds));
                    lineNeedsReposition = this.MajorSize(previousElementBounds) != this.MajorSize(currentBounds);
                    countInLine++;
                }
            }

            _elementManager.SetLayoutBoundsForDataIndex(currentIndex, currentBounds);
#if DEBUG && REPEATER_TRACE
            Log.Debug("{Layout} Layout bounds of element {Index} are {X} {Y} {Width} {Height}",
                layoutId, currentIndex, 
                currentBounds.X, currentBounds.Y, currentBounds.Width, currentBounds.Height);
#endif
            previousIndex = currentIndex;
            currentIndex += step;
        }

        // If we did not reach the top or bottom of the extent, we realized one
        // extra item before we knew we were outside the realization window. Do not
        // account for that element in the indicies inside the realization window.
        if (direction == GenerateDirection.Forward)
        {
            var dataCount = _context.ItemCount;
            _lastRealizedDataIndexInsideRealizationWindow = previousIndex == dataCount - 1 ? dataCount - 1 : previousIndex - 1;
            _lastRealizedDataIndexInsideRealizationWindow = Math.Max(0, _lastRealizedDataIndexInsideRealizationWindow);
        }
        else
        {
            var dataCount = _context.ItemCount;
            _firstRealizedDataIndexInsideRealizationWindow = previousIndex == 0 ? 0 : previousIndex + 1;
            _firstRealizedDataIndexInsideRealizationWindow = Math.Min(dataCount - 1, _firstRealizedDataIndexInsideRealizationWindow);
        }

        _elementManager.DiscardElementsOutsideWindow(direction == GenerateDirection.Forward, currentIndex);
    }

    private bool IsReflowRequired()
    {
        // If first element is realized and is not at the very beginning we need to reflow.
        return _elementManager.GetRealizedElementCount() > 0 &&
            _elementManager.GetDataIndexFromRealizedRangeIndex(0) == 0 &&
            (ScrollOrientation == ScrollOrientation.Vertical ?
            _elementManager.GetLayoutBoundsForRealizedIndex(0).X != 0 :
            _elementManager.GetLayoutBoundsForRealizedIndex(0).Y != 0);
    }

    private bool ShouldContinueFillingUpSpace(int index, GenerateDirection direction)
    {
        bool shouldContinue = false;
        if (!IsVirtualizingContext())
        {
            shouldContinue = true;
        }
        else
        {
            var realizationRect = _context.RealizationRect;
            var elementBounds = _elementManager.GetLayoutBoundsForDataIndex(index);

            var elementMajorStart = this.MajorStart(elementBounds);
            var elementMajorEnd = this.MajorEnd(elementBounds);
            var rectMajorStart = this.MajorStart(realizationRect);
            var rectMajorEnd = this.MajorEnd(realizationRect);

            var elementMinorStart = this.MinorStart(elementBounds);
            var elementMinorEnd = this.MinorEnd(elementBounds);
            var rectMinorStart = this.MinorStart(realizationRect);
            var rectMinorEnd = this.MinorEnd(realizationRect);

            // Ensure that both minor and major directions are taken into consideration so that if the scrolling direction
            // is the same as the flow direction we still stop at the end of the viewport rectangle.
            shouldContinue = direction == GenerateDirection.Forward ?
                elementMajorStart < rectMajorEnd && elementMinorStart < rectMinorEnd :
                elementMajorEnd > rectMajorStart && elementMinorEnd > rectMinorStart;
        }

        return shouldContinue;
    }

    private Rect EstimateExtent(Size availableSize, string layoutId)
    {
        Control firstRealizedElement = null;
        Rect firstBounds = default;
        Control lastRealizedElement = null;
        Rect lastBounds = default;
        int firstDataIndex = -1;
        int lastDataIndex = -1;

        if (_elementManager.GetRealizedElementCount() > 0)
        {
            firstRealizedElement = _elementManager.GetAt(0);
            firstBounds = _elementManager.GetLayoutBoundsForRealizedIndex(0);
            firstDataIndex = _elementManager.GetDataIndexFromRealizedRangeIndex(0);

            int last = _elementManager.GetRealizedElementCount() - 1;
            lastRealizedElement = _elementManager.GetAt(last);
            lastDataIndex = _elementManager.GetDataIndexFromRealizedRangeIndex(last);
            lastBounds = _elementManager.GetLayoutBoundsForRealizedIndex(last);
        }

        Rect extent = _algorithmCallbacks.Algorithm_GetExtent(
            availableSize, _context, firstRealizedElement, firstDataIndex, firstBounds,
            lastRealizedElement, lastDataIndex, lastBounds);
#if DEBUG && REPEATER_TRACE
        Log.Debug("{Layout}: Extent: {Rect}", layoutId, extent);
#endif
        return extent;
    }

    private void RaiseLineArranged()
    {
        var realizationRect = RealizationRect();
        if (realizationRect.Width != 0 || realizationRect.Height != 0)
        {
            int realizedElementCount = _elementManager.GetRealizedElementCount();
            if (realizedElementCount > 0)
            {
                Debug.Assert(_firstRealizedDataIndexInsideRealizationWindow != -1 && _lastRealizedDataIndexInsideRealizationWindow != -1);
                int countInLine = 0;
                var previousElementBounds = _elementManager.GetLayoutBoundsForDataIndex(_firstRealizedDataIndexInsideRealizationWindow);
                var currentLineOffset = this.MajorStart(previousElementBounds);
                var currentLineSize = this.MajorSize(previousElementBounds);
                for (int currentDataIndex = _firstRealizedDataIndexInsideRealizationWindow;
                    currentDataIndex <= _lastRealizedDataIndexInsideRealizationWindow; currentDataIndex++)
                {
                    var currentBounds = _elementManager.GetLayoutBoundsForDataIndex(currentDataIndex);
                    if (this.MajorStart(currentBounds) != currentLineOffset)
                    {
                        // Staring a new line
                        _algorithmCallbacks.Algorithm_OnLineArranged(currentDataIndex - countInLine, countInLine, currentLineSize, _context);
                        countInLine = 0;
                        currentLineOffset = this.MajorStart(currentBounds);
                        currentLineSize = 0;
                    }

                    currentLineSize = Math.Max(currentLineSize, this.MajorSize(currentBounds));
                    countInLine++;
                    previousElementBounds = currentBounds;
                }

                _algorithmCallbacks.Algorithm_OnLineArranged(
                    _lastRealizedDataIndexInsideRealizationWindow - countInLine + 1,
                    countInLine, currentLineSize, _context);
            }
        }
    }

    private void ArrangeVirtualizingLayout(Size finalSize, LineAlignment lineAlignment, bool isWrapping, string layoutId)
    {
        // Walk through the realized elements one line at a time and
        // align them, Then call element.Arrange with the arranged bounds.
        int realizedElementCount = _elementManager.GetRealizedElementCount();
        if (realizedElementCount > 0)
        {
            int countInLine = 1;
            var previousElementBounds = _elementManager.GetLayoutBoundsForRealizedIndex(0);
            var currentLineOffset = this.MajorStart(previousElementBounds);
            var spaceAtLineStart = this.MinorStart(previousElementBounds);
            double spaceAtLineEnd = 0;
            double currentLineSize = this.MajorSize(previousElementBounds);
            for (int i = 1; i < realizedElementCount; i++)
            {
                var currentBounds = _elementManager.GetLayoutBoundsForRealizedIndex(i);
                if (this.MajorStart(currentBounds) != currentLineOffset)
                {
                    spaceAtLineEnd = this.Minor(finalSize) - this.MinorStart(previousElementBounds) - this.MinorSize(previousElementBounds);
                    PerformLineAlignment(i - countInLine, countInLine, spaceAtLineStart, spaceAtLineEnd, 
                        currentLineSize, lineAlignment, isWrapping, finalSize, layoutId);
                    spaceAtLineStart = this.MinorStart(currentBounds);
                    countInLine = 0;
                    currentLineOffset = this.MajorStart(currentBounds);
                    currentLineSize = 0;
                }

                countInLine++; // for current element
                currentLineSize = Math.Max(currentLineSize, this.MajorSize(currentBounds));
                previousElementBounds = currentBounds;
            }

            // Last line - potentially have a property to customize
            // aligning the last line or not.
            if (countInLine > 0)
            {
                double spaceAtEnd = this.Minor(finalSize) - this.MinorStart(previousElementBounds) - this.MinorSize(previousElementBounds);
                PerformLineAlignment(realizedElementCount - countInLine, countInLine, 
                    spaceAtLineStart, spaceAtEnd, currentLineSize, lineAlignment, 
                    isWrapping, finalSize, layoutId);
            }
        }
    }

    // Align elements within a line. Note that this does not modify LayoutBounds. So if we get
    // repeated measures, the LayoutBounds remain the same in each layout.
    private void PerformLineAlignment(int lineStartIndex, int countInLine, double spaceAtLineStart,
        double spaceAtLineEnd, double lineSize, LineAlignment lineAlignment, bool isWrapping,
        Size finalSize, string layoutId)
    {
        for (int rangeIndex = lineStartIndex; rangeIndex < lineStartIndex + countInLine; ++rangeIndex)
        {
            Rect bounds = _elementManager.GetLayoutBoundsForRealizedIndex(rangeIndex);
            this.SetMajorSize(ref bounds, lineSize);

            if (!_scrollOrientationSameAsFlow)
            {
                // Note: Space at start could potentially be negative
                if (spaceAtLineStart != 0 || spaceAtLineEnd != 0)
                {
                    double totalSpace = spaceAtLineStart + spaceAtLineEnd;
                    switch (lineAlignment)
                    {
                        case LineAlignment.Start:
                            {
                                this.SetMinorStart(ref bounds, this.MinorStart(bounds) - spaceAtLineStart);
                                break;
                            }

                        case LineAlignment.End:
                            {
                                this.SetMinorStart(ref bounds, this.MinorStart(bounds) + spaceAtLineEnd);
                                break;
                            }

                        case LineAlignment.Center:
                            {
                                var minor = this.MinorStart(bounds);
                                this.SetMinorStart(ref bounds, minor - spaceAtLineStart);
                                this.SetMinorStart(ref bounds, minor + totalSpace / 2);
                                break;
                            }

                        case LineAlignment.SpaceAround:
                            {
                                double interItemSpace = countInLine >= 1 ? totalSpace / (countInLine * 2) : 0;
                                var minor = this.MinorStart(bounds);
                                this.SetMinorStart(ref bounds, minor - spaceAtLineStart);
                                this.SetMinorStart(ref bounds, minor + interItemSpace * ((rangeIndex - lineStartIndex + 1) * 2 - 1));
                                break;
                            }

                        case LineAlignment.SpaceBetween:
                            {
                                double interItemSpace = countInLine > 1 ? totalSpace / (countInLine - 1) : 0;
                                var minor = this.MinorStart(bounds);
                                this.SetMinorStart(ref bounds, minor - spaceAtLineStart) ;
                                this.SetMinorStart(ref bounds, minor + interItemSpace * (rangeIndex - lineStartIndex));
                                break;
                            }

                        case LineAlignment.SpaceEvenly:
                            {
                                double interItemSpace = countInLine >= 1 ? totalSpace / (countInLine + 1) : 0;
                                var minor = this.MinorStart(bounds);
                                this.SetMinorStart(ref bounds, minor - spaceAtLineStart);
                                this.SetMinorStart(ref bounds, minor + interItemSpace * (rangeIndex - lineStartIndex + 1));
                                break;
                            }
                    }
                }
            }

            bounds = new Rect(bounds.X - _lastExtent.X, bounds.Y - _lastExtent.Y,
                bounds.Width, bounds.Height);

            if (!isWrapping)
            {
                this.SetMinorSize(ref bounds, Math.Max(this.MinorSize(bounds), this.Minor(finalSize)));
            }

            var element = _elementManager.GetAt(rangeIndex);

#if DEBUG && REPEATER_TRACE
            Log.Debug("{Layout}: Arranging element {Index} at {Bounds}",
                layoutId,
                _elementManager.GetDataIndexFromRealizedRangeIndex(rangeIndex),
                bounds);
#endif
            element.Arrange(bounds);
        }
    }

    private Rect RealizationRect() =>
        IsVirtualizingContext() ? _context.RealizationRect :
        new Rect(0, 0, double.PositiveInfinity, double.PositiveInfinity);

    private void SetLayoutOrigin()
    {
        if (IsVirtualizingContext())
        {
            _context.LayoutOrigin = _lastExtent.Position;
        }
        else
        {
            // Should have 0 origin for non-virtualizing layout since we always start from
            // the first item
            Debug.Assert(_lastExtent.X == 0 && _lastExtent.Y == 0);
        }
    }

    internal Control GetElementIfRealized(int dataIndex)
    {
        if (_elementManager.IsDataIndexRealized(dataIndex))
            return _elementManager.GetRealizedElement(dataIndex);

        return null;
    }

    internal bool TryAddElement0(Control element)
    {
        if (_elementManager.GetRealizedElementCount() == 0)
        {
            _elementManager.Add(element, 0);
            return true;
        }

        return false;
    }

    private bool IsVirtualizingContext()
    {
        if (_context != null)
        {
            Rect rect = _context.RealizationRect;
            bool hasInfiniteSize = double.IsInfinity(rect.Height) || double.IsInfinity(rect.Width);
            return !hasInfiniteSize;
        }

        return false;
    }


    private readonly ElementManager _elementManager = new ElementManager();
    private Size _lastAvailableSize;
    private double _lastItemSpacing;
    private bool _collectionChangePending;
    private VirtualizingLayoutContext _context;
    private IFlowLayoutAlgorithmDelegates _algorithmCallbacks;
    private Rect _lastExtent;
    private int _firstRealizedDataIndexInsideRealizationWindow = -1;
    private int _lastRealizedDataIndexInsideRealizationWindow = -1;

    // If the scroll orientation is the same as the folow orientation
    // we will only have one line since we will never wrap. In that case
    // we do not want to align the line. We could potentially switch the
    // meaning of line alignment in this case, but I'll hold off on that
    // feature until someone asks for it - This is not a common scenario
    // anyway.
    private bool _scrollOrientationSameAsFlow;

    public enum LineAlignment
    {
        Start,
        Center,
        End,
        SpaceAround,
        SpaceBetween,
        SpaceEvenly
    }
}
internal enum GenerateDirection
{
    Forward,
    Backward
}
