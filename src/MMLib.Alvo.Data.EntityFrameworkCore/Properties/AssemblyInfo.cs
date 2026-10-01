using System.Runtime.CompilerServices;

// The two shipped drivers, for AlvoSqlStringLiteral alone: a literal quoter is not a contract to publish — public, it
// would invite a host to interpolate a value it should bind — so it is shared with the drivers that write a computed
// field's text constants into DDL and with nobody else.
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.PostgreSql")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.Sqlite.Tests.Integration")]
[assembly: InternalsVisibleTo("MMLib.Alvo.Data.PostgreSql.Tests.Integration")]
