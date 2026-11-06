namespace MMLib.Alvo.Reads;

/// <summary>Shapes each row a read returns: it may only hide a field or fill a field the descriptor declares virtual.</summary>
/// <typeparam name="T">The row's type: a class marked with <c>[AlvoEntity("…")]</c>, or <see cref="Data.AlvoRecord"/>.</typeparam>
public interface IRecordEnricher<T>
    where T : class
{
    /// <summary>Shapes one returned row, synchronously, after the caller's rules and masks.</summary>
    /// <param name="row">The row and the two moves allowed on it.</param>
    void Enrich(Enrich<T> row);
}
