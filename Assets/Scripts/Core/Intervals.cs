using System;
using System.Collections.Generic;

namespace Shimeji.Core
{
    /// <summary>1차원 닫힌 구간 [A, B] 집합 연산.</summary>
    public static class Intervals
    {
        public struct Span
        {
            public float A, B;
            public Span(float a, float b) { A = a; B = b; }
            public float Length => B - A;
            public override string ToString() => $"[{A},{B}]";
        }

        /// <summary>spans에서 [a, b]를 제거한 결과를 반환한다.</summary>
        public static List<Span> Subtract(List<Span> spans, float a, float b)
        {
            if (b <= a) return spans;
            var result = new List<Span>(spans.Count + 1);
            foreach (var s in spans)
            {
                if (b <= s.A || a >= s.B) { result.Add(s); continue; }
                if (a > s.A) result.Add(new Span(s.A, a));
                if (b < s.B) result.Add(new Span(b, s.B));
            }
            return result;
        }

        /// <summary>spans와 clips 합집합의 교집합.</summary>
        public static List<Span> IntersectWithUnion(List<Span> spans, List<Span> clips)
        {
            var result = new List<Span>();
            foreach (var s in spans)
            {
                foreach (var c in clips)
                {
                    float a = Math.Max(s.A, c.A), b = Math.Min(s.B, c.B);
                    if (b > a) result.Add(new Span(a, b));
                }
            }
            return Merge(result);
        }

        public static List<Span> Merge(List<Span> spans)
        {
            if (spans.Count <= 1) return spans;
            spans.Sort((x, y) => x.A.CompareTo(y.A));
            var result = new List<Span> { spans[0] };
            for (int i = 1; i < spans.Count; i++)
            {
                var last = result[result.Count - 1];
                if (spans[i].A <= last.B) { last.B = Math.Max(last.B, spans[i].B); result[result.Count - 1] = last; }
                else result.Add(spans[i]);
            }
            return result;
        }
    }
}
