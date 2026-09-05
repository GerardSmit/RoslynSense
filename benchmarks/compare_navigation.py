#!/usr/bin/env python3
"""Sequential, counterbalanced LSP comparisons; never builds or changes sources."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--updated', type=Path, required=True)
    parser.add_argument('--repository', type=Path, default=Path(r'D:\Sources\Dnn.Platform'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--pairs', type=int, default=3)
    parser.add_argument('--modes', nargs='+', choices=['full', 'lazy'], default=['full', 'lazy'])
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    args.output = args.output.resolve()
    args.repository = args.repository.resolve(strict=True)
    if (args.output / 'comparison.json').exists():
        parser.error('Refusing to overwrite an existing comparison report')
    if len(set(args.modes)) != len(args.modes):
        parser.error('modes must not contain duplicates')
    if args.pairs < 1:
        parser.error('pairs must be positive')
    environment_keys = ('DOTNET_gcServer', 'DOTNET_PROCESSOR_COUNT', 'DOTNET_GCHeapCount',
                        'DOTNET_TC_QuickJitForLoops', 'ROSLYNMCP_EVAL_TIMING')
    if any(os.environ.get(key) for key in environment_keys):
        parser.error('Clean comparison requires no runtime or diagnostic overrides: ' +
                     repr({key: os.environ.get(key) for key in environment_keys}))
    args.output.mkdir(parents=True, exist_ok=True)
    servers = {label: path.resolve(strict=True) for label, path in
               [('baseline', args.baseline), ('updated', args.updated)]}
    report = {'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'servers': {label: {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
                          for label, path in servers.items()},
              'pairs': args.pairs, 'modes': args.modes, 'warm_repeats': 7,
              'body_repeats': 3, 'settle_seconds': 30,
              'order': 'Odd pairs baseline then updated; even pairs updated then baseline.',
              'summary_method': 'Median and range of per-process medians; raw samples retained.',
              'runtime_environment': {key: os.environ.get(key) for key in environment_keys}, 'runs': []}

    def save():
        grouped = {}
        for run in report['runs']:
            if not run.get('passed'):
                continue
            group = grouped.setdefault(run['mode'], {}).setdefault(run['label'], {})
            for operation, value in run['operation_medians_ms'].items():
                group.setdefault(operation, []).append(value)
            for name, value in run['resources'].items():
                group.setdefault(name, []).append(value)
        report['summary'] = {mode: {label: {operation: {
            'processes': len(values), 'median': statistics.median(values),
            'minimum': min(values), 'maximum': max(values), 'per_process': values}
            for operation, values in group.items()} for label, group in modes.items()}
            for mode, modes in grouped.items()}
        pending = args.output / 'comparison.pending.json'
        pending.write_text(json.dumps(report, indent=2), encoding='utf-8')
        pending.replace(args.output / 'comparison.json')

    save()
    for mode in args.modes:
        for pair in range(1, args.pairs + 1):
            order = ['baseline', 'updated'] if pair % 2 else ['updated', 'baseline']
            for label in order:
                destination = args.output / f'{mode}-pair-{pair}-{label}'
                if destination.exists():
                    raise RuntimeError(f'Refusing to overwrite an existing run: {destination}')
                destination.mkdir()
                command = [sys.executable, str(root / 'benchmarks/lsp_navigation.py'),
                           '--server', str(servers[label]), '--repository', str(args.repository),
                           '--output', str(destination), '--samples', '1', '--warm-repeats', '7',
                           '--body-repeats', '3', '--settle-seconds', '30']
                if mode == 'lazy':
                    command.append('--lazy')
                run = {'mode': mode, 'pair': pair, 'label': label, 'output': str(destination),
                       'command': command, 'started_utc': datetime.datetime.now(datetime.timezone.utc).isoformat()}
                report['runs'].append(run)
                print(f'Start {mode} pair {pair}: {label}', flush=True)
                save()
                with (destination / 'console.log').open('wb') as log:
                    completed = subprocess.run(command, cwd=root, stdout=log, stderr=log,
                                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
                run['exit_code'] = completed.returncode
                run['ended_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
                try:
                    result = json.loads((destination / 'results.json').read_text(encoding='utf-8'))
                    sample = result['samples'][0]
                    run['check_count'] = len(sample['checks'])
                    run['source_files_checked'] = result['repository_source_files_checked']
                    run['sources_unchanged'] = result['repository_sources_unchanged']
                    manifest = json.loads((destination / 'source-manifest.json').read_text(encoding='utf-8'))
                    run['source_manifest_sha256'] = hashlib.sha256(json.dumps(
                        manifest, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest()
                    first_manifest = report.setdefault('source_manifest_sha256', run['source_manifest_sha256'])
                    run['passed'] = completed.returncode == 0 and run['check_count'] == 177 and (
                        run['sources_unchanged'] and sample.get('source_unchanged') and not sample.get('error')
                        and not sample.get('cleanup_errors') and not sample['shutdown']['forced']
                        and not sample['shutdown'].get('graceful_error')
                        and run['source_manifest_sha256'] == first_manifest)
                    run['operation_medians_ms'] = {name: metric['median_ms'] for name, metric in sample['summary'].items()}
                    body_times = [metric['milliseconds'] for metric in sample['metrics'] if metric['operation'] == 'body_edit_completion']
                    run['body_edit_sequence_ms'] = body_times
                    run['operation_medians_ms']['first_body_edit_completion'] = body_times[0]
                    memory = sample['memory_final']
                    run['resources'] = {'final_private_mib': memory['private_bytes'] / 1048576,
                                        'final_working_set_mib': memory['working_set_bytes'] / 1048576,
                                        'final_cpu_seconds': (memory['cpu_user_ms'] + memory['cpu_kernel_ms']) / 1000}
                    run['server_sha256'] = result['server_sha256']
                    if run['server_sha256'] != report['servers'][label]['sha256']:
                        run['passed'] = False
                except Exception as error:
                    run['passed'] = False
                    run['report_error'] = str(error)
                save()
                print(f'End {mode} pair {pair} {label}: passed={run["passed"]}; {run.get("operation_medians_ms", {})}', flush=True)
                if not run['passed']:
                    raise SystemExit(1)
    report['completed_utc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    save()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
