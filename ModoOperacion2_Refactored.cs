// ============================================================
// BLOQUE REFACTORIZADO: ModoDeOperacion == "2"
// Va dentro de DoExecute, en el if (cyb00035lc.ModoDeOperacion.Equals("2"))
//
// EQUIVALENCIAS CON CÓDIGO ANTERIOR:
// - switch codTipoCuentaDestino → TipoCuentaMapper.Mapear() (ya en UtilesAndHelpers)
// - string tipo = codTipoCuentaDestino == "G" ? "TIXCTAFO" : "TIXCTAFD" → lógica en BuildQueryModo2
// - ServiceQueryStringGenerator + From("CYBERDTA", "ITCYBFAV") → QueryBuilder<TCYBFAV> directo
// - EasyCrudDataModels.SelectExecute → Dapper QueryAsync<TCYBFAV>
// - foreach con diccionarios ProductosGS/ProductosTP → MapearRegistrosModo2() acceso directo a propiedades
// - PropiedadesAdicionales(registro).AdicionalesSeguros() → GenerarPropiedadesSeguros() método privado
// - PropiedadesAdicionales(cybi0060cl).GetProps() → GenerarPropiedadesProducto() método privado
// - CrearParametros.GetData2/GetData → new Data { }
// - Paginación manual con contadores → Skip/Take
// ============================================================


// ── 1. Mapear tipo de cuenta ──
// ANTES: switch con 5 cases
// AHORA: TipoCuentaMapper en UtilesAndHelpers (ya refactorizado)
codTipoCuentaDestino = TipoCuentaMapper.Mapear(codTipoCuentaDestino);

// ── 2. Construir y ejecutar query ──
// ANTES: ServiceQueryStringGenerator + From("CYBERDTA", "ITCYBFAV") + lógica TIXCTAFO/TIXCTAFD
// AHORA: QueryBuilder<TCYBFAV> directo, DB2 elige el índice
var db2Options = new QueryBuilderOptions
{
    Dialect = SqlDialect.Db2,
    Library = "CYBERDTA",
    TrimStrings = true,
};

var queryAs = BuildQueryModo2(db2Options, numeroIdentificacion, codTipoCuentaDestino).Build();

Stopwatch sw = Stopwatch.StartNew();
var database = new OdbcConnection($"Driver={{{_configManager.GetString("AS400ODBC.driver")}}};System={_configManager.GetString("AS400Connection.Host")};...");
_logger.LogInformation("Intentando abrir la conexión ODBC");
await database.OpenAsync();
sw.Stop();
_logger.LogInformation("Duracion de la conexion a la base de datos {Elapsed}ms", sw.Elapsed.TotalMilliseconds);

sw.Restart();
var registrosQueryResult = (await database.QueryAsync<TCYBFAV>(
    queryAs.Sql,
    queryAs.ToDapperParameters())).ToList();
sw.Stop();
_logger.LogInformation("Duracion de la query TCYBFAV {Elapsed}ms", sw.Elapsed.TotalMilliseconds);


// ── 3. Validación temprana ──
// ANTES: if (!registrosQueryResult.Count.Equals(0)) { ... } else { setear error }
//        (to-do José León: invertir el if para flujo limpio)
// AHORA: se valida primero, si está vacío retorna error y sale
if (!registrosQueryResult.Any())
{
    cyb00035lc.CodMsgRespuesta = "180306";
    cyb00035lc.MsgRespuesta = "No posee productos inscritos para esta funcionalidad";
    cyb00035lc.CaracterAceptacion = "M";

    dataResponse = ServiceDataMapper.MapToResponse(cyb00035lc);
    return dataResponse;
}


// ── 4. Paginación ──
// ANTES: contadores manuales i >= inicio && i <= final dentro del foreach
// AHORA: Skip/Take. Tamaño de página 255 (así estaba en el código original)
var archivoContinuacion = string.IsNullOrWhiteSpace(cyb00035lc.RegistroSiguiente)
    ? 0
    : int.Parse(cyb00035lc.RegistroSiguiente);

const int TAMANO_PAGINA = 255;
var totalRegistros = registrosQueryResult.Count;

var registrosPaginados = registrosQueryResult
    .Skip(archivoContinuacion)
    .Take(TAMANO_PAGINA)
    .ToList();

// Cálculo de valores de paginación para la respuesta
if (archivoContinuacion == 0)
{
    cyb00035lc.ArchivoContinuacion = totalRegistros > TAMANO_PAGINA
        ? (TAMANO_PAGINA + 1).ToString()
        : "0";
    cyb00035lc.ValTalonRegistroAnterior = "0";
    cyb00035lc.CantidadRegistros = totalRegistros.ToString();
}
else
{
    var registrosRestantes = totalRegistros - archivoContinuacion;
    cyb00035lc.CantidadRegistros = registrosRestantes.ToString();

    if (registrosRestantes > TAMANO_PAGINA)
    {
        cyb00035lc.ValTalonRegistroAnterior = archivoContinuacion.ToString();
        cyb00035lc.ArchivoContinuacion = (archivoContinuacion + TAMANO_PAGINA).ToString();
    }
    else
    {
        cyb00035lc.ArchivoContinuacion = "0";
        var talonAnterior = (archivoContinuacion - TAMANO_PAGINA) <= 1
            ? 0
            : (archivoContinuacion - TAMANO_PAGINA);
        cyb00035lc.ValTalonRegistroAnterior = talonAnterior.ToString();
    }
}


// ── 5. Mapear a estructura Data ──
// ANTES: foreach con diccionarios ProductosGS/ProductosTP + PropiedadesAdicionales
// AHORA: MapearRegistrosModo2() que accede directo a las propiedades de TCYBFAV
var registros = MapearRegistrosModo2(registrosPaginados, archivoContinuacion);

dataResponse = ServiceDataMapper.MapToResponse(cyb00035lc);
dataResponse.DataList.Add(registros);


// ============================================================
// MÉTODOS PRIVADOS - agregar a InfrastructureRepository
// ============================================================

/// <summary>
/// Construye la query para ModoDeOperacion 2.
/// Consulta tabla TCYBFAV con filtro condicional por tipo de cuenta.
///
/// REEMPLAZA: ServiceQueryStringGenerator + From("CYBERDTA", "ITCYBFAV")
/// CAMBIO CLAVE: el campo dinámico TIXCTAFO/TIXCTAFD se resuelve aquí.
/// - Si es seguro ("G") → filtra por TipoCuentaOrigen (TIXCTAFO)
/// - Si es otro tipo → filtra por TipoCuentaDestino (TIXCTAFD)
/// - Si es "A" (todos) → no filtra por tipo
/// </summary>
private QueryBuilder<TCYBFAV> BuildQueryModo2(
    QueryBuilderOptions db2Options,
    string numeroIdentificacion,
    string codTipoCuentaDestino)
{
    var query = new QueryBuilder<TCYBFAV>(db2Options)
        .Select()
        .Where(item => item.NumeroIdentidadOrigen == numeroIdentificacion);

    // ANTES: string tipo = codTipoCuentaDestino == "G" ? "TIXCTAFO" : "TIXCTAFD"
    //        if (!codTipoCuentaDestino.Equals("A")) { WhereAnd(TIXCTAFD, "=") }
    // AHORA: misma lógica pero con propiedades tipadas
    if (codTipoCuentaDestino != "A")
    {
        if (codTipoCuentaDestino == "G")
            query = query.Where(item => item.TipoCuentaOrigen == codTipoCuentaDestino);
        else
            query = query.Where(item => item.TipoCuentaDestino == codTipoCuentaDestino);
    }

    return query.OrderByDescending(item => item.TipoCuentaDestino);
}


/// <summary>
/// Mapea registros TCYBFAV a la estructura Data/DataList.
/// Maneja dos variantes: seguros (TipoCuentaOrigen == "G") y productos normales.
///
/// REEMPLAZA: el foreach con diccionarios ProductosGS/ProductosTP del código viejo.
/// ANTES: por cada registro se elegía un diccionario, se recorría con foreach
///        para mapear campo AS400 → nombre XML, y se usaba un switch para setear CYBI0060CL.
/// AHORA: acceso directo a propiedades de TCYBFAV. Sin diccionarios, sin CYBI0060CL.
///
/// Los campos de ProductosGS y ProductosTP son los mismos pero en orden diferente.
/// Ambos mapean a los mismos campos XML de salida.
/// </summary>
private static Data MapearRegistrosModo2(
    List<TCYBFAV> registros,
    int inicioIndex)
{
    var lista = new Data
    {
        HasData = true,
        Field = "Registros",
        Id = "0",
        DataList = new List<Data>()
    };

    for (int i = 0; i < registros.Count; i++)
    {
        var item = registros[i];
        var index = inicioIndex + i + 1;
        var esSeguro = item.TipoCuentaOrigen?.Trim() == "G";

        var registro = new Data
        {
            HasData = true,
            Field = "Registro",
            Id = index.ToString(),
            DataList = new List<Data>()
        };

        // ── Campos principales ──
        // ANTES: foreach sobre diccionario ProductosGS o ProductosTP
        //        registro.DataList.Add(CrearParametros.GetData(field.Trim(), value.Trim()))
        // AHORA: acceso directo a propiedades. Ambos diccionarios mapeaban los mismos
        //        campos XML, solo variaba el orden y los nombres de columna AS400.
        //        Con TCYBFAV y LinqForge, las propiedades ya tienen nombres legibles.

        var codigoDeBanco = new Data { HasData = true, Field = "codigoDeBanco", Value = item.CodigoBanco?.Trim() };
        var numeroDeCuenta = new Data { HasData = true, Field = "numeroDeCuenta", Value = item.NumeroCuentaDestino?.Trim() };
        var valBancoDestino = new Data { HasData = true, Field = "valBancoDestino", Value = item.BancoDestino?.Trim() };
        var descripcion = new Data { HasData = true, Field = "descripcion", Value = item.ValEstado?.Trim() };
        var valFechaHoraCreacion = new Data { HasData = true, Field = "valFechaHoraCreacion", Value = item.FechaHoraCreacion?.Trim() };
        var codMoneda = new Data { HasData = true, Field = "codMoneda", Value = item.ModenaDestino?.Trim() };
        var alias = new Data { HasData = true, Field = "alias", Value = item.AliasCuenta?.Trim() };
        var nombreCliente = new Data { HasData = true, Field = "nombreCliente", Value = item.NombreTitular?.Trim() };
        var numIdentificacionCuenta = new Data { HasData = true, Field = "numeroDeIdentificacionCuenta", Value = item.NumeroIdentidadDestino?.Trim() };
        var valCuentaOrigen = new Data { HasData = true, Field = "valCuentaOrigen", Value = item.NumeroCuentaOrigen?.Trim() };
        var codTipoTransaccion = new Data { HasData = true, Field = "codTipoTransaccion", Value = item.CodTipoTransaccion?.Trim() };
        var guid = new Data { HasData = true, Field = "guid", Value = item.IdentificadorUnico?.Trim() };
        var tipoCtaDestino = new Data { HasData = true, Field = "tipoCtaDestino", Value = item.TipoCuentaDestino?.Trim() };
        var tipoCtaOrigen = new Data { HasData = true, Field = "tipoCtaOrigen", Value = item.TipoCuentaOrigen?.Trim() };
        var monedaOrigen = new Data { HasData = true, Field = "monedaOrigen", Value = item.MonedaOrigen?.Trim() };

        registro.DataList.Add(codigoDeBanco);
        registro.DataList.Add(numeroDeCuenta);
        registro.DataList.Add(valBancoDestino);
        registro.DataList.Add(descripcion);
        registro.DataList.Add(valFechaHoraCreacion);
        registro.DataList.Add(codMoneda);
        registro.DataList.Add(alias);
        registro.DataList.Add(nombreCliente);
        registro.DataList.Add(numIdentificacionCuenta);
        registro.DataList.Add(valCuentaOrigen);
        registro.DataList.Add(codTipoTransaccion);
        registro.DataList.Add(guid);
        registro.DataList.Add(tipoCtaDestino);
        registro.DataList.Add(tipoCtaOrigen);
        registro.DataList.Add(monedaOrigen);

        // ── Propiedades adicionales ──
        // ANTES: if (seguro == "G") { AdicionalesSeguros() } else { GetProps() }
        // AHORA: dos métodos separados según tipo de producto
        if (esSeguro)
            registro.DataList.Add(GenerarPropiedadesSeguros(item));
        else
            registro.DataList.Add(GenerarPropiedadesProducto(item));

        lista.DataList.Add(registro);
    }

    return lista;
}


/// <summary>
/// Genera PropiedadesAdicionales para productos de seguros ("G").
///
/// REEMPLAZA: PropiedadesAdicionales(registro).AdicionalesSeguros()
/// ANTES: recibía EasyMappingTool, usaba GetValue("TIXCTAFD"), GetValue("NUXCTAD"), etc.
/// AHORA: accede directo a propiedades de TCYBFAV.
/// </summary>
private static Data GenerarPropiedadesSeguros(TCYBFAV item)
{
    var props = new Data
    {
        Field = "PropiedadesAdicionales",
        DataList = new List<Data>()
    };

    props.DataList.Add(GenProp("tipo seguro", item.TipoCuentaDestino?.Trim()));
    props.DataList.Add(GenProp("POLIZA", item.NumeroCuentaDestino?.Trim()));
    props.DataList.Add(GenProp("Expediente", item.CodigoBanco?.Trim()));
    props.DataList.Add(GenProp("valCuentaOrigen", item.NumeroCuentaOrigen?.Trim()));
    props.DataList.Add(GenProp("monedaOrigen", item.MonedaOrigen?.Trim()));
    props.DataList.Add(GenProp("tipoProducto", item.TipoCuentaDestino?.Trim()));

    return props;
}


/// <summary>
/// Genera PropiedadesAdicionales para productos normales (no seguros).
///
/// REEMPLAZA: PropiedadesAdicionales(cybi0060cl).GetProps()
/// ANTES: instanciaba CYBI0060CL, seteaba valores con switch dentro del foreach
///        del diccionario, luego llamaba GetProps() que leía de CYBI0060CL.
/// AHORA: lee directo de TCYBFAV. El switch + CYBI0060CL eran un intermediario
///        innecesario — los valores vienen del mismo registro.
///
/// El mapeo de familia/grupo es el mismo que en el bloque else (CYBFAV):
/// "1" → CTA01, "6" → CTA02, otro → CTA03
/// </summary>
private static Data GenerarPropiedadesProducto(TCYBFAV item)
{
    var props = new Data
    {
        Field = "PropiedadesAdicionales",
        DataList = new List<Data>()
    };

    props.DataList.Add(GenProp("valCuentaOrigen", item.NumeroCuentaOrigen?.Trim()));

    var tipoCuenta = item.TipoCuentaDestino?.Trim();
    var (grupo, codigoProducto) = tipoCuenta switch
    {
        "1" => ("CTA01", "1"),
        "6" => ("CTA02", "6"),
        _ => ("CTA03", tipoCuenta ?? "")
    };

    props.DataList.Add(GenProp("familia", "CTAS"));
    props.DataList.Add(GenProp("grupo", grupo));
    props.DataList.Add(GenProp("codigoProducto", codigoProducto));
    props.DataList.Add(GenProp("monedaOrigen", item.MonedaOrigen?.Trim()));
    props.DataList.Add(GenProp("tipoCtaDestino", item.CodTipoTransaccion?.Trim()));
    props.DataList.Add(GenProp("tipoProductoDestino", item.TipoCuentaDestino?.Trim()));
    props.DataList.Add(GenProp("tipoCtaOrigen", item.TipoCuentaOrigen?.Trim()));

    return props;
}


// NOTA: GenProp() es el mismo helper que ya tenés del bloque else.
// No lo dupliques — ya está definido como método privado estático.
