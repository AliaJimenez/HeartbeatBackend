// Las pruebas de la API levantan un host por clase con su propia base SQLite y, al terminar, llaman a
// SqliteConnection.ClearAllPools(), que afecta a todo el proceso. Si dos clases corren a la vez, una cierra las
// conexiones que la otra aun usa y salen ObjectDisposedException al azar (CI en rojo sin que cambie ningun codigo).
// Las pruebas no comparten estado, asi que basta con ejecutar las clases una tras otra.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
