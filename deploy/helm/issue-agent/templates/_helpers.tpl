{{- define "issue-agent.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- define "issue-agent.fullname" -}}
{{- printf "%s-%s" .Release.Name (include "issue-agent.name" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- define "issue-agent.authBrokerFullname" -}}
{{- printf "%s-auth-broker" (include "issue-agent.fullname" . | trunc 51 | trimSuffix "-") }}
{{- end }}
{{- define "issue-agent.selectorLabels" -}}
app.kubernetes.io/name: {{ include "issue-agent.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}
{{- define "issue-agent.labels" -}}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version | replace "+" "_" }}
{{ include "issue-agent.selectorLabels" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}
