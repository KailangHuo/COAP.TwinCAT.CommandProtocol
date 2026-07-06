using System.Runtime.InteropServices;

namespace CoapClient
{
    public class AckPredicate<T> : IAck where T : unmanaged
    {
        public AckPredicate(string ackVarName, PredicateType predicateType, object arg1 = null,
                            object arg2 = null)
            : this(ackVarName, AckPredicateFactory.CreatePredicate<T>(predicateType, arg1, arg2))
        {
            this.Description = $"Var: {this.AckVarName}, " +
                               $"Predicate: {predicateType.ToString()}, " +
                               $"args: {arg1}, {arg2},";
        }

        public AckPredicate(string ackVarName, Func<T, bool> predicate, string description = null)
        {
            this.AckVarName = ackVarName;
            this.Predicate = predicate;
            this.Description = description ?? $"Var:{this.AckVarName}, custom predicate";
        }
    
        private string Description;

        private Func<T, bool> Predicate;

        public string AckVarName { get; }

        public bool IsMatched(byte[] tAckValue)
        {
            T value = MemoryMarshal.Read<T>(tAckValue);
            return this.Predicate(value);
        }
    
        public object GetResultWithType(byte[] rawData)
        {
            T value = MemoryMarshal.Read<T>(rawData);
            return value;
        }

        public override string ToString()
        {
            return this.Description;
        }
    }
}