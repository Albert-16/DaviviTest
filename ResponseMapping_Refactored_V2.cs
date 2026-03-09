// ============================================================
// BLOQUE REFACTORIZADO: Mapeo de respuesta
// Va dentro de DoExecute, después de obtener responseConsult
//
// EQUIVALENCIAS CON CÓDIGO ANTERIOR:
// - ServiceQueryStringGenerator + EasyCrudDataModels → QueryBuilder<CYBFAVC> + Dapper
// - MapData() del Mapper de Sunip → MapearRegistros() método privado
// - PropiedadesAdicionales.GetProps2() → GenerarPropiedadesAdicionales() método privado
// - ResponseDataMapper.Map() → ServiceDataMapper.MapToResponse()
// - EasyMappingTool.GetValue("CAMPO") → item.Propiedad (acceso directo)
// - CrearParametros.GetData("field", value, true) → new Data { Field, Value, HasData }
// - LINQ sobre lista completa de correos por cada registro → Dictionary pre-agrupado O(1)
// - Paginación manual con contadores → Skip/Take
// - Validación post-mapeo → Validación temprana (to-do de José León)
// ============================================================


// ── 1. Consultar correos asociados ──
// ANTES: SelectAll + From("CYBERDTA", "ICYBFAVC03") + EasyCrudDataModels.SelectExecute
// AHORA: QueryBuilder<CYBFAVC> + Dapper async (misma tabla, sin índice específico)
var queryCorreos = new QueryBuilder<CYBFAVC>(db2Options)
    .Select()
    .Where(item => item.NumeroIdentidadOrigen == numeroIdentificacion)
    .Build();

var correos = await database.QueryAsync<CYBFAVC>(
    queryCorreos.Sql,
    queryCorreos.ToDapperParameters());

// ANTES: en MapData, por cada registro se hacía:
//   temp = (from item1 in temp where item1.GetValue("GUIDCOR") == gid1 select item1).ToList()
//   Eso recorría TODOS los correos por cada registro → O(n × m)
// AHORA: se pre-agrupan en Dictionary por GUID → lookup O(1) por registro
var correosPorGuid = correos
    .GroupBy(c => c.IdentificadorUnico)
    .ToDictionary(g => g.Key, g => g.ToList());


// ── 2. Filtrar por tipo de producto ──
// ANTES: dentro de MapData → if (codTipoCuentaDestino == "T" || codTipoCuentaDestino == item.GetValue("CODTIPTRN"))
// AHORA: se filtra antes de mapear, separando responsabilidades
var registrosFiltrados = responseConsult
    .Where(item => codTipoCuentaDestino == "T"
                || codTipoCuentaDestino == item.CodTipoTransaccion)
    .ToList();


// ── 3. Validación temprana ──
// ANTES: se mapeaba TODO con MapData, después se buscaba si el tag "Registro" existía
//        en tags.DataList, y si era null seteaba el error.
//        (to-do de José León: "esta validación no es necesario que se compruebe de esta forma")
// AHORA: se valida directo sobre la lista filtrada. Si está vacía, no hay razón para mapear.
if (!registrosFiltrados.Any())
{
    cyb00035lc.CodMsgRespuesta = "180306";
    cyb00035lc.MsgRespuesta = "No posee productos inscritos para esta funcionalidad";
    cyb00035lc.CaracterAceptacion = "M";

    dataResponse = ServiceDataMapper.MapToResponse(cyb00035lc);
    return dataResponse;
}


// ── 4. Paginación ──
// ANTES: variable archivoContinuacion parseada con múltiples ifs, contadores manuales
//        index++, if (index >= contador && index <= contaFinal) dentro del foreach
// AHORA: Skip/Take sobre la lista ya filtrada. Misma lógica de página de 250.
var archivoContinuacion = string.IsNullOrWhiteSpace(cyb00035lc.RegistroSiguiente)
    ? 0
    : int.Parse(cyb00035lc.RegistroSiguiente);

const int TAMANO_PAGINA = 250;
var totalRegistros = registrosFiltrados.Count;

// Skip salta los registros ya vistos, Take toma solo 250
var registrosPaginados = registrosFiltrados
    .Skip(archivoContinuacion)
    .Take(TAMANO_PAGINA)
    .ToList();

// Cálculo de paginación para la respuesta
// ANTES: bloques if/else con archivoContinuacion string, ParseInt, SetValue
// AHORA: misma lógica pero con acceso directo a propiedades
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
// ANTES: nameTagMapper.MapData(response, TableDictionaries.CybFavDictionary(), "Registro", ...)
//        El Mapper de Sunip instanciaba CYBI0060CL, recorría con foreach,
//        usaba GetValue/SetValue, y mezclaba filtrado + paginación + mapeo
// AHORA: MapearRegistros() solo mapea. El filtrado y paginación ya se hicieron arriba.
var tags = MapearRegistros(registrosPaginados, correosPorGuid, archivoContinuacion);

dataResponse = ServiceDataMapper.MapToResponse(cyb00035lc);
dataResponse.DataList.Add(tags);


// ============================================================
// MÉTODOS PRIVADOS - agregar a InfrastructureRepository
// ============================================================

/// <summary>
/// Mapea registros CYBFAV a la estructura Data/DataList que el consumidor espera en XML.
/// 
/// REEMPLAZA: MapData() del Mapper de Sunip.
/// CAMBIOS CLAVE:
/// - Ya no recibe EasyMappingTool, recibe CYBFAV directamente
/// - Ya no filtra ni pagina (eso se hace antes de llamar este método)
/// - Ya no usa ref int cant (el conteo se obtiene de registrosFiltrados.Count)
/// - Ya no instancia CYBI0060CL ni PropiedadesAdicionales como clase
/// - Los correos se buscan por Dictionary en vez de LINQ sobre lista completa
/// </summary>
private static Data MapearRegistros(
    List<CYBFAV> registros,
    Dictionary<string, List<CYBFAVC>> correosPorGuid,
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

        var registro = new Data
        {
            HasData = true,
            Field = "Registro",
            Id = index.ToString(),
            DataList = new List<Data>()
        };

        // ── Campos principales ──
        // ANTES: CrearParametros.GetData("campo", item.GetValue("COLUMNA"), true)
        // AHORA: new Data { } con acceso directo a la propiedad del record CYBFAV

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

        // ── Correos asociados ──
        // ANTES: temp = (from item1 in temp where item1.GetValue("GUIDCOR") == gid1 ...)
        //        foreach sobre temp creando Data con GetValue("FAVMAIL")
        // AHORA: lookup O(1) en Dictionary pre-agrupado
        registro.DataList.Add(MapearCorreos(item.IdentificadorUnico, correosPorGuid));

        // ── Propiedades adicionales ──
        // ANTES: new PropiedadesAdicionales(cybi0060cl).GetProps2(item)
        //        Instanciaba CYBI0060CL, usaba GetValue sobre EasyMappingTool
        // AHORA: método estático que recibe CYBFAV directamente
        registro.DataList.Add(GenerarPropiedadesAdicionales(item));

        lista.DataList.Add(registro);
    }

    return lista;
}


/// <summary>
/// Busca correos por GUID y los mapea a estructura Data para el XML.
/// 
/// REEMPLAZA: el bloque dentro de MapData que hacía:
///   temp = (from item1 in temp where item1.GetValue("GUIDCOR") == gid1 select item1).ToList();
///   foreach (EasyMappingTool cor in temp) { ... cor.GetValue("FAVMAIL") ... }
///
/// MEJORA: usa Dictionary.TryGetValue O(1) en vez de filtrar lista completa O(n) por registro.
/// </summary>
private static Data MapearCorreos(
    string guid,
    Dictionary<string, List<CYBFAVC>> correosPorGuid)
{
    var emails = new Data
    {
        HasData = true,
        Field = "Emails",
        DataList = new List<Data>()
    };

    if (correosPorGuid.TryGetValue(guid, out var correosDelRegistro))
    {
        foreach (var correo in correosDelRegistro)
        {
            var valEmail = new Data
            {
                HasData = true,
                Field = "valEmail",
                Value = correo.Correo
            };

            var email = new Data
            {
                HasData = true,
                Field = "Email",
                DataList = new List<Data> { valEmail }
            };

            emails.DataList.Add(email);
        }
    }

    return emails;
}


/// <summary>
/// Genera el nodo PropiedadesAdicionales para cada registro en el XML.
///
/// REEMPLAZA: clase PropiedadesAdicionales completa + método GetProps2().
/// ANTES: instanciaba PropiedadesAdicionales con CYBI0060CL, llamaba GetProps2(EasyMappingTool)
///        que usaba item.GetValue("TIPCTAFD"), item.GetValue("NUMCTAO"), etc.
/// AHORA: método estático que accede directo a las propiedades del record CYBFAV.
///        El mapeo de familia/grupo usa switch expression en vez de 3 if-else.
/// </summary>
private static Data GenerarPropiedadesAdicionales(CYBFAV item)
{
    var props = new Data
    {
        Field = "PropiedadesAdicionales",
        DataList = new List<Data>()
    };

    // valCuentaOrigen — ANTES: item.GetValue("NUMCTAO")
    var valCuentaOrigen = new Data
    {
        HasData = true,
        Field = "PropiedadAdicional",
        DataList = new List<Data>
        {
            new Data { HasData = true, Field = "valNombre", Value = "valCuentaOrigen" },
            new Data { HasData = true, Field = "valValor", Value = item.NumeroCuentaOrigen?.Trim() }
        }
    };
    props.DataList.Add(valCuentaOrigen);

    // Familia y grupo según tipo de cuenta destino
    // ANTES: 3 bloques if-else comparando codTipoCuentaDestino.Trim() == "1", "6", else
    // AHORA: switch expression con tuple deconstruction
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


/// <summary>
/// Helper para crear nodos PropiedadAdicional con estructura valNombre/valValor.
/// REEMPLAZA: método GenProp() de la clase PropiedadesAdicionales.
/// Misma estructura, solo se movió aquí como método estático privado.
/// </summary>
private static Data GenProp(string nombre, string valor) => new Data
{
    HasData = true,
    Field = "PropiedadAdicional",
    DataList = new List<Data>
    {
        new Data { HasData = true, Field = "valNombre", Value = nombre },
        new Data { HasData = true, Field = "valValor", Value = valor }
    }
};
