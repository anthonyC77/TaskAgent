"""Validateur JSON Schema minimal, stdlib uniquement.

Sous-ensemble suffisant pour les schémas générés par gen_tool_schemas.py et le
contrat de run : type, properties, required, additionalProperties, items,
enum, const, minimum/maximum, minLength, $ref local (#/…). Tout mot-clé inconnu est ignoré.

Un $ref remplace entièrement son nœud : les mots-clés voisins d'un $ref sont ignorés
(2020-12 les appliquerait en plus) — les schémas du harnais n'en placent jamais.

    errors = validate(instance, schema)   # liste de chaînes "chemin: message", vide si valide
"""

TYPES = {
    "object": dict, "array": list, "string": str, "boolean": bool,
    "integer": int, "number": (int, float), "null": type(None),
}


def _is_type(value, t):
    if t == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if t == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    return isinstance(value, TYPES[t])


def _resoudre(ref, root):
    """Un pointeur JSON local (#/$defs/x). Rien d'autre : pas de réseau, pas de fichier."""
    if not ref.startswith("#/"):
        raise ValueError(f"$ref non local, non pris en charge : {ref}")
    node = root
    for part in ref[2:].split("/"):
        node = node[part.replace("~1", "/").replace("~0", "~")]
    return node


def validate(instance, schema, path="$", root=None):
    errors = []
    if not isinstance(schema, dict):
        return errors
    if root is None:
        root = schema
    while "$ref" in schema:
        schema = _resoudre(schema["$ref"], root)
    t = schema.get("type")
    if t is not None:
        types = t if isinstance(t, list) else [t]
        if not any(_is_type(instance, x) for x in types):
            errors.append(f"{path}: attendu {'|'.join(types)}, reçu {type(instance).__name__}")
            return errors
    if "enum" in schema and instance not in schema["enum"]:
        errors.append(f"{path}: valeur {instance!r} hors de {schema['enum']}")
    if "const" in schema and instance != schema["const"]:
        errors.append(f"{path}: attendu {schema['const']!r}, reçu {instance!r}")
    if isinstance(instance, str) and "minLength" in schema and len(instance) < schema["minLength"]:
        errors.append(f"{path}: longueur < {schema['minLength']}")
    if isinstance(instance, (int, float)) and not isinstance(instance, bool):
        if "minimum" in schema and instance < schema["minimum"]:
            errors.append(f"{path}: {instance} < minimum {schema['minimum']}")
        if "maximum" in schema and instance > schema["maximum"]:
            errors.append(f"{path}: {instance} > maximum {schema['maximum']}")
    if isinstance(instance, dict):
        props = schema.get("properties", {})
        for req in schema.get("required", []):
            if req not in instance:
                errors.append(f"{path}: propriété requise absente : {req}")
        for key, val in instance.items():
            if key in props:
                errors.extend(validate(val, props[key], f"{path}.{key}", root))
            elif schema.get("additionalProperties") is False:
                errors.append(f"{path}: propriété inconnue : {key}")
            elif isinstance(schema.get("additionalProperties"), dict):
                errors.extend(validate(val, schema["additionalProperties"], f"{path}.{key}", root))
    if isinstance(instance, list) and "items" in schema:
        for i, val in enumerate(instance):
            errors.extend(validate(val, schema["items"], f"{path}[{i}]", root))
    return errors


def property_exists(schema, dotted):
    """Vrai si le chemin 'a.b.c' existe dans un schéma objet (traverse les tableaux via items)."""
    node = schema
    for part in dotted.split("."):
        while isinstance(node, dict) and node.get("type") == "array":
            node = node.get("items", {})
        props = node.get("properties", {}) if isinstance(node, dict) else {}
        if part not in props:
            return False
        node = props[part]
    return True
