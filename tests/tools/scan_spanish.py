import sys,re,html
pal=r'\b(de|del|la|las|los|el|que|con|para|por|sin|una|uno|se|esta|est[aá]n|switch(?:es)? (?:de|del)|Conectado|Respaldo|Registro|Resultado|Archivo|Carpeta|Contrase|Usuario|Linea|lineas|Aplicar|Pulsa|Revisa|Guardado|Hora|Fecha|Modelo|Serie|Detalle|Evidencias|Bitacora|Paso|Verificaci|Correcto|Falla|Aviso|Observaci|Ninguno|Inicio|Duraci|Responsable|Motivo|Ticket de)\b'
for f in sys.argv[1:]:
    t=open(f,encoding='utf-8',errors='replace').read()
    if f.endswith('.html'):
        t=re.sub(r'(?s)<style.*?</style>','',t); t=re.sub(r'(?s)<pre[^>]*>.*?</pre>','[PRE]',t)
        t=re.sub(r'data:image/[^"]+','',t); t=re.sub(r'<[^>]+>','\n',t); t=html.unescape(t)
    n=0
    for i,l in enumerate(t.splitlines()):
        if re.search(pal,l,re.I) and not re.search(r'(description|banner|remark|username|snmp-server|hostname|logging|ip |interface|vlan|name |% |Erase of|\^)',l):
            print(f.split('/')[-1],i,l.strip()[:150]); n+=1
            if n>40: break
