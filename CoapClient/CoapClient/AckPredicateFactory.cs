using System.Globalization;

namespace CoapClient
{
    internal static class AckPredicateFactory
{
    public static Func<T, bool> CreatePredicate<T>(PredicateType predicateType, object arg1,
        object arg2)
    {
        Type valueType = typeof(T);
        if (valueType == null) throw new ArgumentNullException(nameof(valueType));

        object expected1 = ConvertTo(valueType, arg1);
        object expected2 = ConvertTo(valueType, arg2);

        switch (predicateType) {
            case PredicateType.Equals:
                return v => {
                    object vv = ConvertTo(valueType, v);
                    return Equals(vv, expected1);
                };

            case PredicateType.NotEquals:
                return v => {
                    object vv = ConvertTo(valueType, v);
                    return !Equals(vv, expected1);
                };

            case PredicateType.GreaterOrEqual:
                return v => Compare(ConvertTo(valueType, v), expected1) >= 0;

            case PredicateType.LessOrEqual:
                return v => Compare(ConvertTo(valueType, v), expected1) <= 0;

            case PredicateType.InBetween:
                return v => {
                    object vv = ConvertTo(valueType, v);
                    return Compare(vv, expected1) >= 0 && Compare(vv, expected2) <= 0;
                };

            default:
                throw new NotSupportedException("Unsupported PredicateType: " + predicateType);
        }
    }

    private static object ConvertTo(Type targetType, object value)
    {
        if (value == null) {
            return null;
        }

        if (targetType.IsInstanceOfType(value)) return value;

        // 允许 string/number 转换
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static int Compare(object a, object b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        var ca = a as IComparable;
        if (ca == null)
            throw new InvalidOperationException("Ack value is not comparable: " + a.GetType());

        return ca.CompareTo(b);
    }
}
}